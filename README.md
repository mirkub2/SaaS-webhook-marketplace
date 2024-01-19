# SaaS-webhook-marketplace
 Separate project with webhook for SaaS offer published in Azure marketplace

 Project is updated clone of https://github.com/Azure/mtm-labs/tree/main/saas/lab-code/end/SaaSFunctions.
 Purpose of this clone is testing communication cycle between webhook and SaaS Fullfilment API v2.
 Clone has updated packages and fixed code parts for SaaS Fullfilment API v2.

 Added security control, whether call to webhook is in evidence of SaaS Fullfilment API. This allows to reject:
 - processing of operations already processed before
 - request through validation of content of received payload against SaaS Fullfilment API

Deployment
- deploy to Azure Functions (Windows)
- add these environment variables to Azure Function

      Auth:ApplicationId    (according to Technical Configuration of SaaS offer in Partner Center)
      Auth:TenantId   (according to Technical Configuration of SaaS offer in Partner Center)
      MarketplaceApi:TenantId    (according to Technical Configuration of SaaS offer in Partner Center)
      MarketplaceApi:ClientId (ClientId is ApplicationID) 
      MarketplaceApi:ClientSecret (find this in EntraID/Application Registrations for used ClientId)

  
 
