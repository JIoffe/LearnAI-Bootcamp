// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.AI.Luis;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.Integration;
using Microsoft.Bot.Builder.Integration.AspNet.Core;
using Microsoft.Bot.Configuration;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.PictureBot
{
    /// <summary>
    /// The Startup class configures services and the request pipeline.
    /// </summary>
    public class Startup
    {
        private ILoggerFactory _loggerFactory;
        private bool _isProduction = false;

        public Startup(IHostingEnvironment env)
        {
            _isProduction = env.IsProduction();

            var builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", optional: true)
                .AddEnvironmentVariables();

            Configuration = builder.Build();
        }

        /// <summary>
        /// Gets the configuration that represents a set of key/value application configuration properties.
        /// </summary>
        /// <value>
        /// The <see cref="IConfiguration"/> that represents a set of key/value application configuration properties.
        /// </value>
        public IConfiguration Configuration { get; }

        /// <summary>
        /// This method gets called by the runtime. Use this method to add services to the container.
        /// </summary>
        /// <param name="services">The <see cref="IServiceCollection"/> specifies the contract for a collection of service descriptors.</param>
        /// <seealso cref="IStatePropertyAccessor{T}"/>
        /// <seealso cref="https://docs.microsoft.com/en-us/aspnet/web-api/overview/advanced/dependency-injection"/>
        /// <seealso cref="https://docs.microsoft.com/en-us/azure/bot-service/bot-service-manage-channels?view=azure-bot-service-4.0"/>
        public void ConfigureServices(IServiceCollection services)
        {
            //The original example couples much of bot init into a single anonymous method,
            //but here it is split up for readability and to resolve race conditions

            //Retrieve the secretKey and botFilePath from the app configuration
            var secretKey = Configuration.GetSection("botFileSecret")?.Value;
            var botFilePath = Configuration.GetSection("botFilePath")?.Value;

            //Use BotConfiguration.Load to create an instance of BotConfiguration
            //based on the file path. Use as a default.
            //(Optional) - Throw an Exception if this is null
            //(Optional) - Add this to the services container as a singleton
            var botConfig = BotConfiguration.Load(botFilePath ?? @".\BotConfiguration.bot", secretKey)
                ?? throw new InvalidOperationException("Error reading bot file. Please ensure you have valid botFilePath and botFileSecret set for your environment.");

            services.AddSingleton(botConfig);

            //Use the AddBot<T> extension to IServiceCollection to inject both
            //your bot and BotFrameworkOptions into the services collection
            services.AddBot<PictureBot>(options =>
            {
                //Retrieve the endpoint for the given environment
                //You can use "FindServiceByNameOrId" and cast to EndpointService
                //(optional) - Throw an exception if it cannot be found
                var environment = _isProduction ? "production" : "development";
                var endpointService = botConfig.FindServiceByNameOrId(environment) as EndpointService
                    ?? throw new InvalidOperationException($"The .bot file does not contain an endpoint with name '{environment}'.");

                //Add a SimpleCredentialProvider to the options.
                //This is important for setting up the route that intercepts bot traffic
                options.CredentialProvider = new SimpleCredentialProvider(endpointService.AppId, endpointService.AppPassword);

                //Set up the datastore to hold conversation state data.
                //We will use a MemoryStorage for this example, but production apps
                //should always leverage Azure Blob or CosmosDB storage providers.
                //(Or roll your own!)
                IStorage dataStore = new MemoryStorage();

                //Create the conversation state based on the datastore
                //and add it to the options.
                var conversationState = new ConversationState(dataStore);

                options.State.Add(conversationState);

                //Add the RegExpRecognizer as middleware.
                //Note that this is not included in the Microsoft.Bot.Builder libraries;
                //instead, it is part of this source distribution!

                var middleware = options.Middleware;
                middleware.Add(new RegExpRecognizerMiddleware()
                    .AddIntent("SearchPics", new Regex("search picture(?:s)*(.*)|search pic(?:s)*(.*)", RegexOptions.IgnoreCase))
                    .AddIntent("Share", new Regex("share picture(?:s)*(.*)|share pic(?:s)*(.*)", RegexOptions.IgnoreCase))
                    .AddIntent("Order", new Regex("order picture(?:s)*(.*)|order print(?:s)*(.*)|order pic(?:s)*(.*)", RegexOptions.IgnoreCase))
                    .AddIntent("Help", new Regex("help(.*)", RegexOptions.IgnoreCase)));

                //Since we have external dependencies, something is bound to go wrong!
                //Setup a callback for OnTurnError to log errors and/or send data to the user
                options.OnTurnError = async (context, exception) =>
                {
                    _loggerFactory.CreateLogger<PictureBot>().LogError(exception, $"Uncaught exception in bot!");
                    await context.SendActivityAsync($"Sorry, it looks like something went wrong. Exception: {exception.Message}");
                };
            });

            // Create and register state accesssors.
            // Acessors created here are passed into the IBot-derived class on every turn.
            services.AddSingleton<PictureBotAccessors>(sp =>
            {
                //Retrieve the IOptions<BotFrameworkOptions> from the IServiceProvider
                //(It's added via AddBot!)
                var options = sp.GetRequiredService<IOptions<BotFrameworkOptions>>().Value;
                if (options == null)
                {
                    throw new InvalidOperationException("BotFrameworkOptions must be configured prior to setting up the state accessors");
                }

                //Retrieve the ConversationState.It is the first state "OfType<ConversationState>()"
                var conversationState = options.State.OfType<ConversationState>().FirstOrDefault();
                if (conversationState == null)
                {
                    throw new InvalidOperationException("ConversationState must be defined and added before adding conversation-scoped state accessors.");
                }

                //return a new instance of PictureBotAccessors based on this conversationState.
                //You will have to also add additional properties for PictureState  and DialogState
                //DialogState is provided by BotBuilder; PictureState is from this code base
                var accessors = new PictureBotAccessors(conversationState)
                {
                    PictureState = conversationState.CreateProperty<PictureState>(PictureBotAccessors.PictureStateName),
                    DialogStateAccessor = conversationState.CreateProperty<DialogState>(PictureBotAccessors.DialogStateName)
                };

                return accessors;
            });

            //Add LUIS to the services collection
            //so we can access it at any point in our bot
            services.AddSingleton(sp =>
            {
                var appId = Configuration.GetSection("LuisAppId").Value;
                var key = Configuration.GetSection("LuisKey").Value;
                var endpoint = Configuration.GetSection("LuisEndpoint").Value;

                //Create a new LuisApplication based on these credentials
                var luisApp = new LuisApplication(appId, key, endpoint);

                //Optionally you can also create LuisPredictionOptions which may vary from bot to bot
                var luisPredictionOptions = new LuisPredictionOptions
                {
                    IncludeAllIntents = true,
                };

                // Create and inject the recognizer
                var recognizer = new LuisRecognizer(luisApp, luisPredictionOptions, true, null);
                return recognizer;
            });
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory;

            //Very important - add UseBotFramework()
            //to automatically register the routes to listen for bot traffic
            app.UseDefaultFiles()
                .UseStaticFiles()
                .UseBotFramework();
        }
    }
}