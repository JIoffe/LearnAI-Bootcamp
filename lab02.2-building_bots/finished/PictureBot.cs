// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.CognitiveServices.Search.ImageSearch;
using Microsoft.Azure.Search;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.AI.Luis;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Schema;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PictureBot.Models;
using PictureBot.Responses;
using ApiKeyServiceClientCredentials = Microsoft.Azure.CognitiveServices.Search.ImageSearch.ApiKeyServiceClientCredentials;

namespace Microsoft.PictureBot
{
    /// <summary>
    /// Represents a bot that processes incoming activities.
    /// For each user interaction, an instance of this class is created and the OnTurnAsync method is called.
    /// This is a Transient lifetime service.  Transient lifetime services are created
    /// each time they're requested. For each Activity received, a new instance of this
    /// class is created. Objects that are expensive to construct, or have a lifetime
    /// beyond the single turn, should be carefully managed.
    /// For example, the <see cref="MemoryStorage"/> object and associated
    /// <see cref="IStatePropertyAccessor{T}"/> object are created with a singleton lifetime.
    /// </summary>
    /// <seealso cref="https://docs.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection?view=aspnetcore-2.1"/>
    public class PictureBot : IBot
    {
        private const string MAIN_DIALOG = "mainDialog";
        private const string SEARCH_DIALOG = "searchDialog";
        private const string SEARCH_PROMPT = "searchPrompt";
        private const string BING_PROMPT = "bingPrompt";

        private readonly PictureBotAccessors _accessors;
        private readonly LuisRecognizer _luisRecognizer;
        private readonly ILogger _logger;

        private readonly DialogSet _dialogs;

        /// <summary>
        /// Initializes a new instance of the <see cref="PictureBot"/> class.
        /// </summary>
        /// <param name="accessors">A class containing <see cref="IStatePropertyAccessor{T}"/> used to manage state.</param>
        /// <param name="loggerFactory">A <see cref="ILoggerFactory"/> that is hooked to the Azure App Service provider.</param>
        /// <seealso cref="https://docs.microsoft.com/en-us/aspnet/core/fundamentals/logging/?view=aspnetcore-2.1#windows-eventlog-provider"/>
        public PictureBot(PictureBotAccessors accessors, LuisRecognizer luisRecognizer, ILogger<PictureBot> logger)
        {
            //On each turn, we receive an active instance of PictureBotAccessors, LuisRecognizer, and the logger
            //via dependency injection. We must also recreate the DialogSet on each turn.

            //Assign all the private variables to match the injected values
            _logger = logger;
            _logger.LogTrace("PizzaBot turn start.");
            _accessors = accessors;
            _luisRecognizer = luisRecognizer;

            //Create a new DialogSet based on the DialogStateAccessor of the PictureBotAccessors
            _dialogs = new DialogSet(_accessors.DialogStateAccessor);

            //Add new waterfall dialogs for our "main" and "search" dialogs
            var mainDialogSteps = new WaterfallStep[]
            {
                GreetingAsync,
                MainMenuAsync
            };

            var searchDialogSteps = new WaterfallStep[]
            {
                SearchRequestAsync,
                SearchAsync,
                SearchBingAsync
            };

            _dialogs.Add(new WaterfallDialog(MAIN_DIALOG, mainDialogSteps));
            _dialogs.Add(new WaterfallDialog(SEARCH_DIALOG, searchDialogSteps));

            //Add prompts as well. Bot.Builder comes with several out of the box,
            //including TextPrompt and ConfirmPrompt
            _dialogs.Add(new TextPrompt(SEARCH_PROMPT));
            _dialogs.Add(new ConfirmPrompt(BING_PROMPT));
        }

        /// <summary>
        /// Every conversation turn for our Bot will call this method.
        /// There are no dialogs used, since it's "single turn" processing, meaning a single
        /// request and response.
        /// </summary>
        /// <param name="turnContext">A <see cref="ITurnContext"/> containing all the data needed
        /// for processing this conversation turn. </param>
        /// <param name="cancellationToken">(Optional) A <see cref="CancellationToken"/> that can be used by other objects
        /// or threads to receive notice of cancellation.</param>
        /// <returns>A <see cref="Task"/> that represents the work queued to execute.</returns>
        /// <seealso cref="BotStateSet"/>
        /// <seealso cref="ConversationState"/>
        /// <seealso cref="IMiddleware"/>
        public async Task OnTurnAsync(ITurnContext turnContext, CancellationToken cancellationToken = default(CancellationToken))
        {
            //First, see if the incoming activity type is of type "message" (ActivityTypes.Message)
            //Otherwise, we can safely ignore it
            // see https://aka.ms/about-bot-activity-message to learn more about the message and other activity types

            if (turnContext.Activity.Type == ActivityTypes.Message)
            {
                //Recreate the DialogContext on each turn and continue the active dialog
                var dc = await _dialogs.CreateContextAsync(turnContext, cancellationToken);
                var results = await dc.ContinueDialogAsync(cancellationToken);

                //There are many ways to consider when our bot's dialog stack has been exhausted.
                //One easy way is to see if the Responded property is set to true or false.
                //If we did not respond for the turn, start a new MAIN_DIALOG
                if (!turnContext.Responded)
                {
                    await dc.BeginDialogAsync(MAIN_DIALOG, null, cancellationToken);
                }
            }

            //EXTREMELY IMPORTANT!
            //After each turn, update the conversation state so that
            //Your bot will behave as you expect!
            await _accessors.ConversationState.SaveChangesAsync(turnContext);
        }

        private async Task<DialogTurnResult> GreetingAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            //Retrieve the active state associated with the ITurnContext
            //If no state is set, add a factory method that creates a blank one
            var state = await _accessors.PictureState.GetAsync(stepContext.Context, () => new PictureState());

            //Check to see if we have greeted the user or not.
            //If we have not greeted the user, greet and reply with help!
            //Be sure to update the state to note that we HAVE greeted.

            //In either case, move to the next dialog
            if (!state.HasGreeted)
            {
                await MainResponses.ReplyWithGreeting(stepContext.Context);
                await MainResponses.ReplyWithHelp(stepContext.Context);

                state.HasGreeted = true;
                await _accessors.ConversationState.SaveChangesAsync(stepContext.Context);
            }

            return await stepContext.NextAsync();
        }

        private async Task<DialogTurnResult> MainMenuAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            //Our main menu is essentially the root state of the dialog. Think of it as
            //a hub that lets you access any specific part of the bot.

            //First, retrieve the PictureState or a blank PictureState if it does not exist
            var state = await _accessors.PictureState.GetAsync(stepContext.Context, () => new PictureState());

            //Then, retrieve matching intents from the middleware pipeline via getting IRecognizedIntents from ITurnContext::TurnState
            var intents = stepContext.Context.TurnState.Get<IRecognizedIntents>();

            //Save a reference to the best intent so far
            var bestIntent = intents?.TopIntent?.Name;

            //If we do not have one...
            // - pass our turn context to the LuisRecognizer's RecognizeAsync method
            // - get the top intent as a tuple (string, double)
            // - See if the score exceeds a certain threshold
            // - Use the helper method to extract the "facet" and set it to our active state's search
            // - ** Save Changes to the ConversationState anytime you alter it!!! **
            if (string.IsNullOrWhiteSpace(bestIntent))
            {
                var luisResults = await _luisRecognizer.RecognizeAsync(stepContext.Context, cancellationToken);
                (string luisIntent, double score) = luisResults.GetTopScoringIntent();

                if(score > 0.65)
                {
                    if(luisIntent == "SearchPictures")
                    {
                        var search = GetSearchQuery(luisResults.Entities);
                        if (!string.IsNullOrWhiteSpace(search))
                        {
                            state.Search = search;
                            state.IsSearching = true;
                            await _accessors.ConversationState.SaveChangesAsync(stepContext.Context);
                        }
                    }

                    bestIntent = luisIntent;
                }
            }

            //Whether it's from LUIS or RegExp recognition,
            //Now we need a switch on the best intent to 
            //move the bot. In a large bot, this can cater to
            //any number of actions, dialogs, or RPA.

            //The intents we're using are: Share, Order, Help, SearchPics.
            //The default case should cover anything else.
            //Share,Order, and Help can simply send canned responses,
            //however SearchPics should begin the SEARCH_DIALOG
            switch (bestIntent)
            {
                case "Share":
                    await MainResponses.ReplyWithShareConfirmation(stepContext.Context);
                    break;
                case "Order":
                    await MainResponses.ReplyWithOrderConfirmation(stepContext.Context);
                    break;
                case "Help":
                    await MainResponses.ReplyWithHelp(stepContext.Context);
                    break;
                case "SearchPictures":
                case "SearchPics":
                    return await stepContext.BeginDialogAsync(SEARCH_DIALOG, null, cancellationToken);
                default:
                    await MainResponses.ReplyWithConfused(stepContext.Context);
                    break;
            }

            return await stepContext.EndDialogAsync();
        }

        private async Task<DialogTurnResult> SearchRequestAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            //Access the active state. Since we've done this already, it will already be filled in here
            var state = await _accessors.PictureState.GetAsync(stepContext.Context, () => new PictureState());

            //From here, see if the user is already actively searching.
            //If so, go to the next step.
            //Otherwise, set IsSearching to true and then use SEARCH_PROMPT
            //to ask the user what he or she would like to search for.

            //Hint: You can use MessageFactory to create activities that  can pass into prompts.
            if (state.IsSearching)
            {
                return await stepContext.NextAsync();
            }
            else
            {
                state.IsSearching = true;

                return await stepContext.PromptAsync("searchPrompt", new PromptOptions
                {
                    Prompt = MessageFactory.Text("What would you like to search for?")
                }, cancellationToken);
            }
        }

        private async Task<DialogTurnResult> SearchAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            //Here is the main event! This method will utilize Azure Search
            //to find results that the user asked for.
            var state = await _accessors.PictureState.GetAsync(stepContext.Context, () => new PictureState());

            //We can find the user's search two ways:
            // - As part of the state
            // - As the result coming to this step from a previous (Prompt) dialog

            //First, see if the query can be found on the "Search" property
            //of the state. If not, apply this stepcontext's result (as a string)
            //and update the conversation state
            var q = state.Search;

            if (string.IsNullOrWhiteSpace(q))
            {
                q = stepContext.Result as string;
                state.Search = q;
                await _accessors.ConversationState.SaveChangesAsync(stepContext.Context);
            }

            //Next, establish a client using the helper method.
            //CTRL and click on this helper method to adjust for your service.
            var client = CreateSearchIndexClient();

            //Call SearchAsync on this client's Documents property
            //to retrieve the search results
            var searchResponse = await client.Documents.SearchAsync(q);

            //If the response's results have a count of 0...
            // - Either give a failure message
            // - Or keep going and search on Bing

            if(searchResponse.Results.Count == 0)
            {
                await SearchResponses.ReplyWithNoResults(stepContext.Context, q);
                return await stepContext.PromptAsync("bingPrompt", new PromptOptions
                {
                    Prompt = MessageFactory.Text("Would you like to search on bing?")
                }, cancellationToken);
            }
            else
            {
                //Otherwise, create a new activity to reply with
                IMessageActivity activity = stepContext.Context.Activity.CreateReply();

                //Use the provided SearchHitStyler() class to apply
                //formatted results to the activity.
                //You will want to map the search response Results to a 
                //list of SearchHit objects using the ImageMapper.ToSearchHit method
                var styler = new SearchHitStyler();
                var options = searchResponse.Results.Select(r => ImageMapper.ToSearchHit(r)).ToList().AsReadOnly();

                styler.Apply(ref activity, "Here are the results:", options);

                //Send the activity to the user
                await stepContext.Context.SendActivityAsync(activity, cancellationToken);
            }

            //In any case, reset the search flags
            //(be sure to save the conversation state!)
            //and end the dialog. We are done! This dialog pops
            //off of the stack and we are left with the main dialog.
            state.IsSearching = false;
            state.Search = string.Empty;

            await _accessors.ConversationState.SaveChangesAsync(stepContext.Context);

            return await stepContext.EndDialogAsync();
        }

        private async Task<DialogTurnResult> SearchBingAsync(WaterfallStepContext stepContext, CancellationToken cancellationToken)
        {
            var state = await _accessors.PictureState.GetAsync(stepContext.Context);

            //since we got here via a prompt, the step context's "Result" property
            //should be a true or false value indicating whether the user
            //clicked yes or no.

            //If the user said YES to Bing Search...
            // - create a new instance of Microsoft.Azure.CognitiveServices.Search.ImageSearch.ImageSearchAPI
            // - This ImageSearchAPI instance should use an instance of ApiKeyServiceClientCredentials based on your BING key
            // - pass the same query parameter to the the client's Images property's SearchAsync method. (this is on the state!)
            // - Let us assume all images are PNG. You will want to map (Select =>) some number of these
            //   as an Attachment (Bot.Builder) where the ContentUrl is the image's ContentUrl, and the ContentType is "image/png"
            // - Use MessageFactory to create a carousel of these attachments.
            // - Be sure to use try and Catch! We have our OnTurnError callback but the user should ideally never see it

            //If the user said no...
            // - Respect their choice :)

            //In any case end the dialog
            var choice = (bool)stepContext.Result;
            if (choice)
            {
                var key = "";
                var client = new ImageSearchAPI(new ApiKeyServiceClientCredentials(key));

                try
                {
                    Azure.CognitiveServices.Search.ImageSearch.Models.Images results = await client.Images.SearchAsync(state.Search);
                    var attachments = results.Value.Take(5)
                        .Select(img => new Attachment
                        {
                            ContentUrl = img.ContentUrl,
                            ContentType = "image/png"
                        });

                    var activity = MessageFactory.Attachment(attachments, "We found the following images on Bing:");
                    activity.AttachmentLayout = "carousel";

                    await stepContext.Context.SendActivityAsync(activity, cancellationToken);
                }
                catch (Exception e)
                {
                    await stepContext.Context.SendActivityAsync($"Problem during Bing search: {e.Message}");
                }
            }
            else
            {
                await stepContext.Context.SendActivityAsync("Alright, we will not Bing.");
            }


            //Reset the state
            state.Search = string.Empty;
            state.IsSearching = false;
            await _accessors.ConversationState.SaveChangesAsync(stepContext.Context);

            return await stepContext.EndDialogAsync();
        }

        public string GetSearchQuery(JObject entities)
        {
            //This is taken from the original LearnAI Source code. Note that
            //LuisRecognizer returns a JObject for entities. You can also treat the field
            //as a list to cater to multiple instances of a particular kind of entity
            var obj = JObject.Parse(JsonConvert.SerializeObject(entities)).SelectToken("facet");
            return obj?.ToString().Replace("\"", "").Trim(']', '[').Trim();
        }

        public ISearchIndexClient CreateSearchIndexClient()
        {
            //Replace with your search service values
            string searchServiceName = "";
            string queryApiKey = "";
            string indexName = "images";


            //Create a new instance of Microsoft.Azure.Search.SearchIndexClient 
            //Using your service and index names
            //It should also take a new SearchCredentials based on your queryApiKey
            return new SearchIndexClient(searchServiceName, indexName, new SearchCredentials(queryApiKey));
        }
    }
}
