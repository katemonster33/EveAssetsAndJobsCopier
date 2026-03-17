using ESI.NET;
using ESI.NET.Enumerations;
using ESI.NET.Models.Assets;
using ESI.NET.Models.Industry;
using ESI.NET.Models.Market;
using ESI.NET.Models.SSO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Data.SQLite;
using YamlDotNet;


//using System.Data.SQLite;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Web;
using System.Xml;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace EveMarketBot
{
    public class Worker : BackgroundService
    {
        ILogger<Worker> Logger { get; set; }

        const int numRetries = 10;
        const int theForgeRegionId = 10000002;
        const int essenceRegionId = 10000064;
        const long heydielesHqId = 1039723362469;
        const string stockListsJsonPath = "stockLists.json";

        IEsiClient esiClient;
        public Worker(IEsiClient client, ILogger<Worker> logger)
        {
            esiClient = client;
            Logger = logger;
        }

        public static async Task<AuthorizedCharacterData?> AuthorizeSSO(IEsiClient esiClient)
        {
            const string callbackUrl = "http://localhost:8080/";
            var listener = new HttpListener();
            listener.Prefixes.Add(callbackUrl);
            listener.Start();
            string state = "BLAH";
            Process.Start(new ProcessStartInfo
            {
                FileName = esiClient.SSO.CreateAuthenticationUrl(new List<string>() { "publicData","esi-location.read_location.v1","esi-location.read_ship_type.v1","esi-characters.read_contacts.v1","esi-corporations.read_corporation_membership.v1","esi-assets.read_assets.v1","esi-characters.write_contacts.v1","esi-corporations.read_structures.v1","esi-characters.read_loyalty.v1","esi-characters.read_chat_channels.v1","esi-characters.read_medals.v1","esi-characters.read_standings.v1","esi-characters.read_agents_research.v1","esi-industry.read_character_jobs.v1","esi-characters.read_blueprints.v1","esi-characters.read_corporation_roles.v1","esi-location.read_online.v1","esi-characters.read_fatigue.v1","esi-corporations.track_members.v1","esi-characters.read_notifications.v1","esi-corporations.read_divisions.v1","esi-corporations.read_contacts.v1","esi-assets.read_corporation_assets.v1","esi-corporations.read_titles.v1","esi-corporations.read_blueprints.v1","esi-corporations.read_standings.v1","esi-corporations.read_starbases.v1","esi-industry.read_corporation_jobs.v1","esi-corporations.read_container_logs.v1","esi-industry.read_character_mining.v1","esi-industry.read_corporation_mining.v1","esi-corporations.read_facilities.v1","esi-corporations.read_medals.v1","esi-characters.read_titles.v1","esi-characters.read_fw_stats.v1","esi-corporations.read_fw_stats.v1","esi-corporations.read_projects.v1","esi-corporations.read_freelance_jobs.v1","esi-characters.read_freelance_jobs.v1","esi-activities.read_character.v1" }, state),
                UseShellExecute = true
            });
            bool runServer = true;

            string? ssoResponse = null;
            // While a user hasn't visited the `shutdown` url, keep on handling requests
            while (runServer)
            {
                // Will wait here until we hear from a connection
                HttpListenerContext ctx = await listener.GetContextAsync();

                // If `shutdown` url requested w/ POST, then shutdown the server after serving the page
                if (ctx.Request.HttpMethod == "GET" && ctx.Request.Url?.AbsolutePath == "/callback")
                {
                    var pairs = HttpUtility.ParseQueryString(ctx.Request.Url.Query);
                    if(pairs != null) ssoResponse = pairs["code"];
                    string responseString = "<HTML><BODY> Congratulations! You've logged in! EveMarketBot liked that! You can close this tab.</BODY></HTML>";
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseString);
                    // Get a response stream and write the response to it.
                    ctx.Response.ContentLength64 = buffer.Length;
                    ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    // You must close the output stream.
                    ctx.Response.OutputStream.Close();
                    break;
                }
            }

            listener.Stop();
            listener.Close();
            if (ssoResponse != null)
            {
                SsoToken token = await esiClient.SSO.GetToken(GrantType.AuthorizationCode, ssoResponse);
                AuthorizedCharacterData characterData = await esiClient.SSO.Verify(token);
                esiClient.SetCharacterData(characterData);
                return characterData;
            }
            return null;
        }

        Dictionary<int, int> ReadBlueprintQuantities()
        {
            var result = new Dictionary<int, int>();

            using var reader = new StreamReader("blueprints.yaml");

            var yaml = new YamlStream();
            yaml.Load(reader);

            var root = (YamlMappingNode)yaml.Documents[0].RootNode;

            foreach (var entry in root.Children)
            {
                int id = int.Parse(((YamlScalarNode)entry.Key).Value ?? "0");
                var blueprint = (YamlMappingNode)entry.Value;

                if (!blueprint.Children.TryGetValue("activities", out var activitiesNode))
                    continue;

                var activities = (YamlMappingNode)activitiesNode;

                if (activities.Children.TryGetValue("manufacturing", out var manufacturingNode) && manufacturingNode != null)
                {
                    var manufacturing = (YamlMappingNode)manufacturingNode;

                    if (!manufacturing.Children.TryGetValue("products", out var productsNode))
                        continue;

                    var products = (YamlSequenceNode)productsNode;

                    if (products.Children.Count == 0)
                        continue;

                    if (!((YamlMappingNode)products.Children[0]).Children.TryGetValue("quantity", out var quantityNode) || quantityNode == null)
                        continue;

                    int quantity = int.Parse(((YamlScalarNode)quantityNode).Value ?? "0");

                    result[id] = quantity;
                }
                
                if (activities.Children.TryGetValue("reaction", out var reactionNode) && reactionNode != null)
                {
                    if (!((YamlMappingNode)reactionNode).Children.TryGetValue("products", out var productsNode))
                        continue;

                    var products = (YamlSequenceNode)productsNode;

                    if (products.Children.Count == 0)
                        continue;

                    if (!((YamlMappingNode)products.Children[0]).Children.TryGetValue("quantity", out var quantityNode) || quantityNode == null)
                        continue;

                    int quantity = int.Parse(((YamlScalarNode)quantityNode).Value ?? "0");

                    result[id] = quantity;
                }
            }

            return result;
        }

        Dictionary<int, string> GetTypeNameDictionary()
        {
            var result = new Dictionary<int, string>();

            using var reader = new StreamReader("types.yaml");

            var yaml = new YamlStream();
            yaml.Load(reader);

            var root = (YamlMappingNode)yaml.Documents[0].RootNode;

            foreach (var entry in root.Children)
            {
                int id = int.Parse(((YamlScalarNode)entry.Key).Value ?? "0");

                var itemNode = (YamlMappingNode)entry.Value;

                if(itemNode.Children.TryGetValue("published", out var publishedNode) && publishedNode != null)
                {
                    bool isPublished = bool.Parse(((YamlScalarNode) publishedNode).Value ?? "false");
                    if(!isPublished)
                    {
                        continue;
                    }
                }
                
                if (!itemNode.Children.TryGetValue("name", out var nameNode) || nameNode == null)
                    continue;

                if (!((YamlMappingNode)nameNode).Children.TryGetValue("en", out var enNode) || enNode == null)
                    continue;

                string value = ((YamlScalarNode)enNode).Value ?? string.Empty;

                result[id] = value;
            }
            return result;
        }
        
        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            var blueprintsOutputQuantities = ReadBlueprintQuantities();
            var typeIdsToNames = GetTypeNameDictionary();

            var authedCharacter = await AuthorizeSSO(esiClient);
            if (authedCharacter == null)
            {
                Console.WriteLine("Failed to authorize SSO!");
                return;
            }

            var charAssets = await ReadAssets(true);
            var corpAssets = await ReadAssets(false);
            var jobs = await ReadCorpJobs();
            if(charAssets == null)
            {
                Console.WriteLine("Failed to read character assets!");
                return;
            }
            if(corpAssets == null)
            {
                Console.WriteLine("Failed to read corp assets!");
                return;
            }
            if(jobs == null)
            {
                Console.WriteLine("Failed to read jobs!!");
                return;
            }
            List<string> clipboardLines = new List<string>();
            int alpaStationId = 60014707;
            foreach(var item in charAssets)
            {
                if(item.LocationId == alpaStationId && typeIdsToNames.TryGetValue((int)item.TypeId, out var itemName))
                {
                    string itemLine = itemName + " " +  item.Quantity;
                    Console.WriteLine(itemLine);
                    clipboardLines.Add(itemLine);
                }
            }
            foreach(var item in corpAssets)
            {
                if(item.TypeId == 27) continue;
                if(blueprintsOutputQuantities.ContainsKey(item.TypeId)) continue; // is it a blueprint
                if(typeIdsToNames.TryGetValue((int)item.TypeId, out var itemName))
                {
                    string itemLine = itemName + " " +  item.Quantity;
                    Console.WriteLine(itemLine);
                    clipboardLines.Add(itemLine);
                }
            }
            foreach(var job in jobs)
            {
                string jobLine = typeIdsToNames[job.ProductTypeId] + " " + blueprintsOutputQuantities[job.BlueprintTypeId] * job.Runs;
                Console.WriteLine(jobLine);
                clipboardLines.Add(jobLine);
            }
            TextCopy.ClipboardService.SetText(string.Join(Environment.NewLine, clipboardLines));
        }

        private async Task<List<Job>?> ReadCorpJobs()
        {
            Console.WriteLine("Reading corporation jobs...");
            var output = new List<Job>();
            EsiResponse<List<Job>>? jobListResp = null;
            int numPages = 2;
            for (int pageId = 1; pageId <= numPages; pageId++)
            {
                int retry = 0;
                for (; retry < numRetries; retry++)
                {
                    jobListResp = await esiClient.Industry.JobsForCorporation(false, pageId);
                    if (jobListResp.StatusCode == HttpStatusCode.OK)
                    {
                        Thread.Sleep(100); // be nice to the server
                        break;
                    }
                    else
                    {
                        Logger.LogWarning("Waiting and repeating request...");
                        Thread.Sleep(500); // be extra nice to the server
                    }
                }
                if(retry >= numRetries || jobListResp == null)
                {
                    return null;
                }
                if (pageId == 1)
                {
                    numPages = jobListResp.Pages ?? 0;
                }                
                output.AddRange(jobListResp.Data);
            }
            return output;
        }
        

        private async Task<List<Item>?> ReadAssets(bool charAssets)
        {
            Logger.LogInformation("Reading assets for " + (charAssets ? "character" : "corporation") + "...");
            int numPages = 2;
            List<Item> output = new List<Item>();
            EsiResponse<List<Item>>? assetListResp = null;
            for (int pageId = 1; pageId <= numPages; pageId++)
            {
                int retry = 0;
                for (; retry < numRetries; retry++)
                {
                    if(charAssets)
                    {
                        assetListResp = await esiClient.Assets.ForCharacter(pageId);
                    }
                    else
                    {
                        assetListResp = await esiClient.Assets.ForCorporation(pageId);
                    }
                    if (assetListResp.StatusCode == HttpStatusCode.OK)
                    {
                        Thread.Sleep(100); // be nice to the server
                        break;
                    }
                    else
                    {
                        Logger.LogWarning("Waiting and repeating request...");
                        Thread.Sleep(500); // be extra nice to the server
                    }
                }
                if(retry >= numRetries || assetListResp == null)
                {
                    return null;
                }
                if (pageId == 1)
                {
                    numPages = assetListResp.Pages ?? 0;
                }                
                output.AddRange(assetListResp.Data);
            }
            return output;
        }
    }
}
