// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Azure.Management.AppService.Fluent;
using Microsoft.Azure.Management.CosmosDB.Fluent;
using Microsoft.Azure.Management.CosmosDB.Fluent.Models;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.KeyVault.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Management.Samples.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ManageWebAppCosmosDbByMsi
{
    public class Program
    {
        /**
         * Azure App Service basic sample for managing web apps.
         *  - Create a Cosmos DB with credentials stored in a Key Vault
         *  - Create a web app which interacts with the Cosmos DB by first
         *      reading the secrets from the Key Vault.
         *
         *      The source code of the web app is located at Asset/documentdb-dotnet-todo-app
         */

        public static void RunSample(IAzure azure)
        {
            Region region = Region.USWest;
            string appName = SdkContext.RandomResourceName("webapp-", 20);
            string rgName = SdkContext.RandomResourceName("rg1NEMV_", 24);
            string vaultName = SdkContext.RandomResourceName("vault", 20);
            string cosmosName = SdkContext.RandomResourceName("cosmosdb", 20);
            string appUrl = appName + ".azurewebsites.net";

            try
            {
                //============================================================
                // Create a CosmosDB

                Utilities.Log("Creating a CosmosDB...");
                ICosmosDBAccount cosmosDBAccount = azure.CosmosDBAccounts.Define(cosmosName)
                        .WithRegion(region)
                        .WithNewResourceGroup(rgName)
                        .WithKind(DatabaseAccountKind.GlobalDocumentDB)
                        .WithEventualConsistency()
                        .WithWriteReplication(Region.USEast)
                        .WithReadReplication(Region.USCentral)
                        .Create();

                Utilities.Log("Created CosmosDB");
                Utilities.Log(cosmosDBAccount);

                //============================================================
                // Create a key vault

                Utilities.Log("Createing an Azure Key Vault...");
                var servicePrincipalInfo = ParseAuthFile(Environment.GetEnvironmentVariable("AZURE_AUTH_LOCATION"));

                IVault vault = azure.Vaults
                        .Define(vaultName)
                        .WithRegion(region)
                        .WithNewResourceGroup(rgName)
                        .DefineAccessPolicy()
                            .ForServicePrincipal(servicePrincipalInfo.ClientId)
                            .AllowSecretAllPermissions()
                            .Attach()
                        .Create();

                SdkContext.DelayProvider.Delay(10000);
                Utilities.Log("Created Azure Key Vault");
                Utilities.Log(vault.Name);

                //============================================================
                // Store Cosmos DB credentials in Key Vault

                // Parse the auth file's clientId, clientSecret, and tenantId to ClientSecretCredential
                ClientSecretCredential credential = ParseAuthFileToCredential(Environment.GetEnvironmentVariable("AZURE_AUTH_LOCATION"));

                var client = new SecretClient(new Uri(vault.VaultUri), credential);

                client.SetSecretAsync("azure-documentdb-uri", cosmosDBAccount.DocumentEndpoint);
                client.SetSecretAsync("azure-documentdb-key", cosmosDBAccount.ListKeys().PrimaryMasterKey);
                client.SetSecretAsync("azure-documentdb-database", "tododb");

                //============================================================
                // Create a web app with a new app service plan

                Utilities.Log("Creating web app " + appName + " in resource group " + rgName + "...");

                IWebApp app = azure.WebApps
                        .Define(appName)
                        .WithRegion(region)
                        .WithExistingResourceGroup(rgName)
                        .WithNewWindowsPlan(PricingTier.StandardS1)
                        .WithNetFrameworkVersion(NetFrameworkVersion.V4_6)
                        .WithAppSetting("AZURE_KEYVAULT_URI", vault.VaultUri)
                        .WithSystemAssignedManagedServiceIdentity()
                        .Create();

                Utilities.Log("Created web app " + app.Name);
                Utilities.Log(app);

                //============================================================
                // Update vault to allow the web app to access

                vault.Update()
                        .DefineAccessPolicy()
                            .ForObjectId(app.SystemAssignedManagedServiceIdentityPrincipalId)
                            .AllowSecretAllPermissions()
                            .Attach()
                        .Apply();

                //============================================================
                // Deploy to web app through local Git

                Utilities.Log("Deploying a local asp.net application to " + appName + " through Git...");

                var profile = app.GetPublishingProfile();
                Utilities.DeployByGit(profile, "documentdb-dotnet-todo-app");

                Utilities.Log("Deployment to web app " + app.Name + " completed");
                Utilities.Print(app);

                // warm up
                Utilities.Log("Warming up " + appUrl + "...");
                Utilities.CheckAddress("http://" + appUrl);
                SdkContext.DelayProvider.Delay(5000);
                Utilities.Log("CURLing " + appUrl + "...");
                Utilities.Log(Utilities.CheckAddress("http://" + appUrl));
            }
            finally
            {
                try
                {
                    Utilities.Log("Deleting Resource Group: " + rgName);
                    azure.ResourceGroups.DeleteByName(rgName);
                    Utilities.Log("Deleted Resource Group: " + rgName);
                }
                catch (NullReferenceException)
                {
                    Utilities.Log("Did not create any resources in Azure. No clean up is necessary");
                }
                catch (Exception g)
                {
                    Utilities.Log(g);
                }
            }
        }

        public static void Main(string[] args)
        {
            try
            {
                //=================================================================
                // Authenticate
                var credentials = SdkContext.AzureCredentialsFactory.FromFile(Environment.GetEnvironmentVariable("AZURE_AUTH_LOCATION"));

                var azure = Microsoft.Azure.Management.Fluent.Azure
                    .Configure()
                    .WithLogLevel(HttpLoggingDelegatingHandler.Level.Basic)
                    .Authenticate(credentials)
                    .WithDefaultSubscription();

                // Print selected subscription
                Utilities.Log("Selected subscription: " + azure.SubscriptionId);

                RunSample(azure);
            }
            catch (Exception e)
            {
                Utilities.Log(e);
            }
        }

        private static ServicePrincipalLoginInformation ParseAuthFile(string authFile)
        {
            var info = new ServicePrincipalLoginInformation();
            string tenantId = string.Empty;

            var lines = File.ReadLines(authFile);
            if (lines.First().Trim().StartsWith("{"))
            {
                string json = string.Join("", lines);
                var jsonConfig = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                info.ClientId = jsonConfig["clientId"];

                if (jsonConfig.ContainsKey("clientSecret"))
                {
                    info.ClientSecret = jsonConfig["clientSecret"];
                }
            }
            else
            {
                lines.All(line =>
                {
                    if (line.Trim().StartsWith("#"))
                        return true; // Ignore comments
                    var keyVal = line.Trim().Split(new char[] { '=' }, 2);
                    if (keyVal.Length < 2)
                        return true; // Ignore lines that don't look like $$$=$$$
                    if (keyVal[0].Equals("client", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ClientId = keyVal[1];
                    }
                    if (keyVal[0].Equals("key", StringComparison.OrdinalIgnoreCase))
                    {
                        info.ClientSecret = keyVal[1];
                    }
                    return true;
                });
            }

            return info;
        }

        private static ClientSecretCredential ParseAuthFileToCredential(string authFile)
        {
            var authData = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(authFile));

            authData.TryGetValue("clientId", out string clientId);
            authData.TryGetValue("clientSecret", out string clientSecret);
            authData.TryGetValue("tenantId", out string tenantId);

            ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);

            return credential;
        }
    }
}