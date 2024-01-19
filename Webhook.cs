using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using Microsoft.Marketplace.SaaS;
using Azure.Identity;
using Microsoft.Marketplace.SaaS.Models;
using System.Threading;
using System.Buffers;

namespace SaasFunctions
{
    public static class Webhook
    {
        private static HttpRequest _request = null;
        private static ILogger _logger = null;
        private static Microsoft.Azure.WebJobs.ExecutionContext _executionContext = null;

        /// <summary>
        /// This function is called by Azure when an event occurs on a subscription in the Azure marketplace.
        /// It needs to be configured in Partner Center to be called by the Azure marketplace.
        /// </summary>
        /// <param name="req">HttpRequest</param>
        /// <param name="log">ILogger</param>
        /// <param name="context">ExecutionContext</param>
        /// <returns>Task<IActionResult></returns>
        [FunctionName("Webhook")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = null)] HttpRequest req,
            ILogger log,
            Microsoft.Azure.WebJobs.ExecutionContext context)
        {
            _request = req;
            _logger = log;
            _executionContext = context;

            PrintToLogHeader();

            //check claim - JWT token 
            if (!RequestIsSecure())
            {
                _logger.LogInformation("Security checks did not pass!");
                return new StatusCodeResult((int)HttpStatusCode.Forbidden);
            }

            var requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            dynamic data = JsonConvert.DeserializeObject(requestBody);


            PrintToLogPayload(data);

            string OperationId_in_payload = data.id;
            string SubscriptionId_in_payload = data.subscriptionId;
            int quantity_in_payload = data.quantity;

            //check validity of call from marketplace to webhook
            //with call to marketplace API from webhook
            try
            {
                var config = new ConfigurationBuilder()
                    .SetBasePath(_executionContext.FunctionAppDirectory)
                    .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables()
                    .Build();
                IMarketplaceSaaSClient _marketplaceSaaSClient;
                var tenantId = config["MarketplaceApi:TenantId"];
                var clientId = config["MarketplaceApi:ClientId"];
                var clientSecret = config["MarketplaceApi:ClientSecret"];
                var creds = new ClientSecretCredential(tenantId, clientId, clientSecret);
                _marketplaceSaaSClient = new MarketplaceSaaSClient(creds);
 
                //you can check more parameters in received payload against evidence in marketplace
                var operationStatus = (await _marketplaceSaaSClient.Operations.GetOperationStatusAsync(new System.Guid(SubscriptionId_in_payload), new System.Guid(OperationId_in_payload))).Value;
                _logger.LogInformation($"Stav: {operationStatus.Status.ToString()} ");
                // Possible values: NotStarted, InProgress, Failed, Succeeded, Conflict

                //example: checking required quantity in ChangeQuantity operation
                if (data.action == "ChangeQuantity" && (operationStatus.Status.ToString().Trim() == "NotStarted" || operationStatus.Status.ToString().Trim() == "InProgress") )
                {
                    if (!(quantity_in_payload == operationStatus.Quantity))
                    {
                        _logger.LogInformation($"Wrong quantity in payload: in marketplace {operationStatus.Quantity.ToString()} in payload {data.quantity.ToString()} ");
                        return new StatusCodeResult((int)HttpStatusCode.Conflict);
                    }
                    else
                    {
                        _logger.LogInformation("Payload check against marketplace evidence: PASSED");
                    }
                } else
                {
                    _logger.LogInformation("Invalid or already processed operation ");
                    return new StatusCodeResult((int)HttpStatusCode.Conflict); 
                }

            }
            catch (System.Exception e)
            {
                _logger.LogInformation($"ErrorGet: {e.Message.Trim()}");
                _logger.LogInformation($"ErrorGet: {e.StackTrace.ToString()}");
                return new StatusCodeResult((int)HttpStatusCode.Conflict);

            }

            
            //everything is OK, send OK also to marketplace to confirm change
            return new OkResult();
        }

        /// <summary>
        /// Calls functions that check various security aspects of this webhook
        /// </summary>
        /// <returns>bool indicating if the request is secure</returns>
        private static bool RequestIsSecure()
        {
            try
            {



                // set up the configuration
                var config = new ConfigurationBuilder()
                    .SetBasePath(_executionContext.FunctionAppDirectory)
                    .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables()
                    .Build();


                if (!ClaimsAreValid(config))
                {
                    _logger.LogInformation("Claims are invalid.");
                    return false;
                }

                return true;
            }
            catch (System.Exception e)
            {
                _logger.LogInformation($"Error: {e.Message.Trim()}");
                _logger.LogInformation($"Error: {e.StackTrace.ToString()}");

                return false;
            }


        }

        /// <summary>
        /// Checks that JWT claims are valid
        /// </summary>
        /// <param name="config">IConfigurationRoot</param>
        /// <returns>A bool indicating if the Claims in the JWT are valid.</returns>
        private static bool ClaimsAreValid(IConfigurationRoot config)
        {
            string authHeader = _request.Headers["Authorization"];
            _logger.LogInformation($"authHeader: {authHeader}");

            var jwt = authHeader.Split(' ')[1];
            var handler = new JwtSecurityTokenHandler();

            if (!handler.CanReadToken(jwt))
            {
                _logger.LogInformation("Can't read JWT");
                return false;
            }

            var jwtToken = handler.ReadToken(jwt) as JwtSecurityToken;

            //_logger.LogInformation($"Can't read JWT {jwtToken.Claims.ToString()}");

            foreach ( var token in jwtToken.Claims ) {
                _logger.LogInformation($"claim type { token.Type}");
                _logger.LogInformation($"claim value {token.Value}");
            }


            var appId = jwtToken.Claims.FirstOrDefault(c => c.Type == "aud").Value;
            var tenantId = jwtToken.Claims.FirstOrDefault(c => c.Type == "tid").Value;
            var issuer = jwtToken.Claims.FirstOrDefault(c => c.Type == "iss").Value;

            if (appId != config["Auth:ApplicationId"])
            {
                _logger.LogInformation("Application ID does not match.");
                _logger.LogInformation($"Configured Application ID: {config["Auth:ApplicationId"]}");
                _logger.LogInformation($"Received Application ID: {appId}");

                return false;
            }

            if (tenantId != config["Auth:TenantId"])
            {
                _logger.LogInformation("Tenant ID does not match.");
                _logger.LogInformation($"Configured Tenant ID: {config["Auth:ApplicationId"]}");
                _logger.LogInformation($"Received Tenant ID: {tenantId}");
                return false;
            }

            var validIssuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
            if (validIssuer != issuer)
            {
                _logger.LogInformation("Issuer does not match.");
                return false;
            }

            return true;
        }


        /// <summary>
        /// Check the event actully occured
        /// </summary>
        /// <param name="data">the JSON paylod s a dynamic value</param>
        /// <returns>Whether of the the reported event is a valid one</returns>
        private static bool SubscriptionEventIsValid(dynamic data)
        {
            try
            {

                switch (data.action)
                {
                    case "Unsubscribed":
                        // 1. Use the SaaS Fulfillment API to fetch the relevant subscription
                        // 2. Check the subscription's "saasSubscriptionStatus": "Unsubscribed"
                        // 3. Return false on unexpected result
                        break;

                    case "ChangePlan":
                        // 1. Use the SaaS Operation API to validate a ChangePlan has been requested
                        // 2. return false on unexpected result
                        break;

                    case "ChangeQuantity":
                        // 1. Use the SaaS Operation API to validate a ChangePlan has been requested
                        // 2. return false on unexpected result
                        break;
                        // etc.
                }

                return true;

            }
            catch (System.Exception e)
            {
                _logger.LogInformation($"Error in validation of event: {e.Message.Trim()}");
                _logger.LogInformation($"Error in validation of event: {e.StackTrace.ToString()}");

                return false;
            }
        }

        private static void PrintToLogHeader()
        {
            _logger.LogInformation("===================================");
            _logger.LogInformation("SaaS WEBHOOK FUNCTION FIRING");
            _logger.LogInformation("-----------------------------------");
        }

        private static void PrintToLogPayload(dynamic data)
        {
            _logger.LogInformation($"ACTION: {data.action}");
            _logger.LogInformation("-----------------------------------");
            _logger.LogInformation((string)data.ToString());
            _logger.LogInformation("===================================");
        }
    }
}
