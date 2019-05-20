using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Google.Cloud.Dialogflow.V2;
using Google.Protobuf.WellKnownTypes;
using planty_compare_fulfillment.Models;
using System.Linq;
using Google.Protobuf;
using System.Net.Http;

namespace planty_compare_fulfillment.dotnet
{
    public static class PlantyCompareHttpTrigger
    {
        private const string HELP_MESSAGE = 
            "You can ask, how much would you need to make in a given city to maintain a comparable lifestyle,"
            + " or, you can say exit... What can I help you with?";
        private const string HELP_REPROMPT = "What can I help you with?";
        private const string STOP_MESSAGE = "Goodbye!";
        private static readonly string INTENT_MAIN = "actions.intent.MAIN";
        private static readonly string INTENT_NOINPUT = "actions.intent.NO_INPUT";

        internal enum PlantyRequestType
        {
            EquivalentIncomeEstimate,
            Help,
            Stop,
            HealthCheck
        }

        private static readonly JsonParser jsonParser = new JsonParser(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
        private static readonly HttpClient client = new HttpClient();

        [FunctionName("PlantyCompareHttpTrigger")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            string name = req.Query["name"];

            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            // dynamic data = JsonConvert.DeserializeObject(requestBody);
            // name = name ?? data?.name;

            // return name != null
            //     ? (ActionResult)new OkObjectResult($"Hello, {name}")
            //     : new BadRequestObjectResult("Please pass a name on the query string or in the request body");

            string responseJson = null;
            string userId = null;
            WebhookRequest request = null;
             PlantyRequestType requestType;

            if (IsHealthCheck(requestBody, log)) {
                requestType = PlantyRequestType.HealthCheck;
            } else {
                try
                {
                    request = jsonParser.Parse<WebhookRequest>(requestBody);
                }
                catch (InvalidProtocolBufferException ex)
                {
                    log.LogError(ex, "Web hook request could not be parsed.");
                    return new BadRequestObjectResult("Error deserializing Dialog flow request from message body");
                }                
                userId = GetUserId(request);
                string intent = request.QueryResult.Intent.DisplayName;
                requestType = GetPlantyRequestType(intent);
            }
            
            WebhookResponse webHookResp = null;

            switch (requestType)
            {
                case PlantyRequestType.EquivalentIncomeEstimate:
                    // TODO: Parameterize targetCurrency too
                    var targetCurrency = request.QueryResult.Parameters
                                        // .Fields["targetCurrency"].StringValue;
                                        .Fields["baseIncome"].StructValue.Fields["currency"].StringValue;

                    decimal incomeAmount = await GetEquivalentIncome(request);
                    string estimateText =
                        $"You'd need to earn {incomeAmount} {targetCurrency}s, to maintain a comparable lifestyle.";
                    webHookResp = GetDialogFlowResponse(userId, estimateText);
                    break;
                case PlantyRequestType.Help:
                    webHookResp = GetDialogFlowResponse(userId, HELP_MESSAGE, HELP_REPROMPT);
                    break;
                case PlantyRequestType.Stop:
                    webHookResp = GetDialogFlowResponse(userId, STOP_MESSAGE);
                    break;
                case PlantyRequestType.HealthCheck:
                    webHookResp = GetDialogFlowResponse(userId, HELP_REPROMPT);
                    break;
            }

            responseJson = webHookResp?.ToString();

            ContentResult contRes = new ContentResult()
            {
                Content = responseJson,
                ContentType = "application/json",
                StatusCode = 200
            };

            return contRes;
        }

        private static async Task<decimal> GetEquivalentIncome(WebhookRequest request)
        {
            var requestFields = request.QueryResult.Parameters.Fields;
            var targetCity = requestFields["targetCity"].StringValue;
            // var targetCurrency = requestFields["targetCurrency"].StringValue;
            var baseCity = requestFields["baseCity"].StringValue;
            var baseIncomeFields = requestFields["baseIncome"].StructValue.Fields;
            var baseIncomeAmount = (int)baseIncomeFields["amount"].NumberValue;
            var baseCurrency = baseIncomeFields["currency"].StringValue;
            // TODO: Parameterize targetCurrency too
            var targetCurrency = baseCurrency;

            var response = await client.GetAsync(
                "http://localhost:5000/api/equivalent-income?"
                + $"targetCity={targetCity}&targetCurrency={targetCurrency}"
                + $"&baseCity={baseCity}&baseIncomeAmount={baseIncomeAmount}&baseCurrency={baseCurrency}");

            var incomeAmount = Decimal.Parse(await response.Content.ReadAsStringAsync());
            return Math.Round(incomeAmount / 100) * 100;
        }

        private static WebhookResponse GetDialogFlowResponse(string userId, string message)
        {
            return GetDialogFlowResponse(userId, message, null);
        }

        private static WebhookResponse GetDialogFlowResponse(string userId, string message, string reprompt)
        {
            bool expectUserResp = !string.IsNullOrWhiteSpace(reprompt);

            WebhookResponse webHookResp = InitializeResponse(expectUserResp, userId);

            var fulfillmentMessage = webHookResp.FulfillmentMessages[0];

            fulfillmentMessage.SimpleResponses = new Intent.Types.Message.Types.SimpleResponses();
            var simpleResp = new Intent.Types.Message.Types.SimpleResponse();
            simpleResp.Ssml = $"<speak>{message}</speak>";           
            fulfillmentMessage.SimpleResponses.SimpleResponses_.Add(simpleResp);

            return webHookResp;
        }

        private static WebhookResponse InitializeResponse(bool expectUserInput, string userId)
        {
            WebhookResponse webResp = new WebhookResponse();

            var message = new Intent.Types.Message();
            webResp.FulfillmentMessages.Add(message);
            message.Platform = Intent.Types.Message.Types.Platform.ActionsOnGoogle;

            // var payload = Struct.Parser.ParseJson("{\"google\": { \"expectUserResponse\": true}} ");

            //message.Payload = new
            Value payloadVal = new Value();
            payloadVal.StructValue = new Struct();

            Value expectedUserResp = new Value();
            expectedUserResp.BoolValue = expectUserInput;
            payloadVal.StructValue.Fields.Add("expectUserResponse", expectedUserResp);

            Value userStorageValue = new Value();

            UserStorage userStorage = new UserStorage();
            userStorage.UserId = userId;
            userStorageValue.StringValue = JsonConvert.SerializeObject(userStorage);

            payloadVal.StructValue.Fields.Add("userStorage", userStorageValue);

            Struct payloadStruct = new Struct();

            payloadStruct.Fields.Add("google", payloadVal);

            webResp.Payload = payloadStruct;

            return webResp;
        }

        private static PlantyRequestType GetPlantyRequestType(string intentName)
        {
            PlantyRequestType plantyReqType = PlantyRequestType.HealthCheck;
           
            if(intentName.Equals("Default Welcome Intent", StringComparison.OrdinalIgnoreCase) ||
                   intentName.Equals("EquivalentIncomeEstimateIntent", StringComparison.OrdinalIgnoreCase))
            {
                plantyReqType = PlantyRequestType.EquivalentIncomeEstimate;
            }
          
            return plantyReqType;
        }
        
        internal static bool IsRepromptRequest(WebhookRequest request)
        {
            bool isRepromptRequest = false;

            ListValue inputs = request.OriginalDetectIntentRequest.Payload?.Fields?["inputs"]?.ListValue;

            if (inputs != null)
            {
                bool isIntentFound = false;

                int valCount = 0;
                while (!isIntentFound && valCount < inputs.Values.Count)
                {
                    Value val = inputs.Values[valCount];

                    if (val.StructValue != null)
                    {
                        if (val.StructValue.Fields["intent"] != null)
                        {
                            string intentName = val.StructValue.Fields["intent"].StringValue;
                            isIntentFound = true;

                            if (intentName.Equals(INTENT_NOINPUT, StringComparison.OrdinalIgnoreCase))
                            {
                                isRepromptRequest = true;
                            }
                        }
                    }
                    valCount++;
                }
            }
            return isRepromptRequest;
        }

        private static string GetUserId(WebhookRequest request)  
        {  
            string userId = null;  
            Struct intentRequestPayload = request.OriginalDetectIntentRequest?.Payload;  
    
            var userStruct = intentRequestPayload.Fields?["user"];  
    
            string userStorageText = null;  
            if ((userStruct?.StructValue?.Fields?.Keys.Contains("userStorage")).GetValueOrDefault(false))  
            {  
                userStorageText = userStruct?.StructValue?.Fields?["userStorage"]?.StringValue;  
            }  
    
            if (!string.IsNullOrWhiteSpace(userStorageText))  
            {  
                UserStorage userStore = JsonConvert.DeserializeObject<UserStorage>(userStorageText);  
                userId  = userStore.UserId;  
            }  
            else  
            {  
                if ((userStruct?.StructValue?.Fields?.Keys.Contains("userId")).GetValueOrDefault(false))  
                {  
                    userId = userStruct?.StructValue?.Fields?["userId"]?.StringValue;  
                }  
    
                if (string.IsNullOrWhiteSpace(userId))  
                {  
                    // The user Id is not provided. Generate a new one and return it.  
                    userId = Guid.NewGuid().ToString("N");  
                }  
            }  
            return userId;  
        }

        public static bool IsHealthCheck(string requestBody, ILogger log)  
        {  
            bool isHealthCheck = false;
            ConversationRequest convReq = null;        
            try
            {  
                convReq = JsonConvert.DeserializeObject<ConversationRequest>(requestBody);  
            }
            catch (Exception ex)  
            {  
                log.LogInformation(ex, "Web hook request is not a health check. Cannot deserialize request as a health check.");  
            }  

            var firstInput = convReq?.Inputs?.FirstOrDefault(x => (x.Intent?.Equals(INTENT_MAIN)).GetValueOrDefault(false));  
            
            if (firstInput != null)  
            {  
                var arg = firstInput.Arguments?.FirstOrDefault(x => (x.Name?.Equals("is_health_check")).GetValueOrDefault(false));  
        
                if (arg != null)  
                {  
                    isHealthCheck = arg.BoolValue;  
                }  
            }
            return isHealthCheck;  
        }
    }
}
