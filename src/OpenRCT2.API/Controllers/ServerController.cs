﻿using System;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using OpenRCT2.API.Abstractions;
using OpenRCT2.API.Extensions;
using OpenRCT2.API.Implementations;
using OpenRCT2.API.JsonModels;
using OpenRCT2.API.Models;
using OpenRCT2.Network;

namespace OpenRCT2.API.Controllers
{
    public class ServerController : Controller
    {
        private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(75);
        private readonly ILogger<ServerController> _logger;

        #region Request / Response Models

        public class JGetServersResponse : JResponse
        {
            public Server[] servers { get; set; }
        }

        public class JAdvertiseServerRequest
        {
            public string key { get; set; }
            public int port { get; set; }
        }

        public class JAdvertiseServerResponse : JResponse
        {
            public string token { get; set; }
        }

        public class JAdvertiseHeartbeatRequest
        {
            public string token { get; set; }
            public int players { get; set; }
            public ServerGameInfo gameInfo { get; set; }
        }

        #endregion

        public ServerController(ILogger<ServerController> logger)
        {
            _logger = logger;
        }

        [Route("servers")]
        [HttpGet]
        public async Task<object> GetServersAsync(
            [FromServices] HttpClient httpClient,
            [FromServices] IServerRepository serverRepository)
        {
            var accept = HttpContext.Request.Headers[HeaderNames.Accept];
            var returnJson = accept.Contains(MimeTypes.ApplicationJson, StringComparer.InvariantCultureIgnoreCase);

            try
            {
                // A good time to clean up any expired servers
                await DoServerCleanupAsync(serverRepository);

                var servers = await serverRepository.GetAllAsync();
                if (returnJson)
                {
                    var response = new JGetServersResponse()
                    {
                        status = JStatus.OK,
                        servers = servers
                    };
                    return ConvertResponse(response);
                }
                else
                {
                    servers = servers
                        .OrderByDescending(x => x.Players)
                        .ThenBy(x => x.RequiresPassword)
                        .ThenByNaturalDescending(x => x.Version)
                        .ThenBy(x => x.Name)
                        .ToArray();
                    return View("Views/Servers.cshtml", servers);
                }
            }
            catch
            {
                var msg = "Unable to connect to fetch servers.";
                if (returnJson)
                {
                    return ConvertResponse(JResponse.Error(msg));
                }
                else
                {
                    return StatusCode(StatusCodes.Status500InternalServerError, msg);
                }
            }
        }

        [Route("servers")]
        [HttpPost]
        public async Task<IJResponse> AdvertiseServerAsync(
            [FromServices] IServerRepository serverRepository,
            [FromServices] Random random,
            [FromBody] JAdvertiseServerRequest body)
        {
            var remoteAddress = GetRemoteAddress();
            if (String.IsNullOrEmpty(remoteAddress))
            {
                return JResponse.Error(JErrorMessages.ServerError);
            }

            Server serverInfo;
            try
            {
                string serverInfoJson;
                using (var client = new OpenRCT2Client())
                {
                    _logger.LogInformation("Connecting to {0}:{1}", remoteAddress, body.port);
                    await client.Connect(remoteAddress, body.port);
                    _logger.LogInformation("Requesting server info from {0}:{1}", remoteAddress, body.port);
                    serverInfoJson = await client.RequestServerInfo();
                }
                serverInfo = JsonConvert.DeserializeObject<Server>(serverInfoJson);
            }
            catch (SocketException)
            {
                return ConvertResponse(JResponse.Error("Unable to connect to server, make sure your ports are open."));
            }
            catch (TimeoutException)
            {
                return ConvertResponse(JResponse.Error("Timed out while waiting for server response."));
            }
            catch
            {
                return ConvertResponse(JResponse.Error("Unable to advertise server."));
            }

            var token = random.NextBytes(8)
                              .ToHexString();
            var server = new Server()
            {
                Token = token,
                LastHeartbeat = DateTime.Now,

                Addresses = new ServerAddressList()
                {
                    IPv4 = new string[] { remoteAddress },
                    IPv6 = new string[0]
                },
                Port = body.port,
                Name = serverInfo.Name,
                Description = serverInfo.Description,
                Provider = serverInfo.Provider,
                RequiresPassword = serverInfo.RequiresPassword,
                Players = serverInfo.Players,
                MaxPlayers = serverInfo.MaxPlayers,
                Version = serverInfo.Version
            };

            _logger.LogInformation("Registering server {0} [{1}:{2}]", serverInfo.Name, remoteAddress, body.port);
            await serverRepository.AddOrUpdateAsync(server);

            var response = new JAdvertiseServerResponse()
            {
                status = JStatus.OK,
                token = token
            };
            return ConvertResponse(response);
        }

        [Route("servers")]
        [HttpPut]
        public async Task<IJResponse> AdvertiseHeartbeatAsync(
            [FromServices] IServerRepository serverRepository,
            [FromBody] JAdvertiseHeartbeatRequest body)
        {
            if (string.IsNullOrEmpty(body?.token))
            {
                return ConvertResponse(JResponse.Error(JErrorMessages.InvalidToken));
            }

            Server server = await serverRepository.GetByTokenAsync(body.token);
            if (server == null)
            {
                return ConvertResponse(JResponse.Error(JErrorMessages.ServerNotRegistered));
            }

            server.Players = body.players;
            server.GameInfo = body.gameInfo;
            server.LastHeartbeat = DateTime.Now;
            await serverRepository.AddOrUpdateAsync(server);

            return ConvertResponse(JResponse.OK());
        }

        private static Task DoServerCleanupAsync(IServerRepository serverRepository)
        {
            DateTime minimumHeartbeatTime = DateTime.Now
                                                    .Subtract(HeartbeatTimeout);
            return serverRepository.RemoveDeadServersAsync(minimumHeartbeatTime);
        }

        private IJResponse ConvertResponse(IJResponse response)
        {
            Version clientVersion = Request.GetOpenRCT2ClientVersion();
            if (clientVersion != null)
            {
                string szStatus = response.status as string;
                switch (szStatus) {
                case JStatus.OK:
                    response.status = 200;
                    break;
                case JStatus.Error:
                    response.status = 500;
                    if (response.message == JErrorMessages.ServerNotRegistered)
                    {
                        response.status = 401;
                    }
                    break;
                default:
                    response.status = 500;
                    break;
                }
            }
            return response;
        }

        private string GetRemoteAddress()
        {
            const string HeaderXForwardedFor = "X-FORWARDED-FOR";

            StringValues forwardedFor;
            if (HttpContext.Request.Headers.TryGetValue(HeaderXForwardedFor, out forwardedFor))
            {
                string[] addresses = forwardedFor[0].Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                return addresses[0].Trim();
            }

            IHttpConnectionFeature connection = HttpContext.Features.Get<IHttpConnectionFeature>();
            if (connection != null)
            {
                return connection.RemoteIpAddress.ToString();
            }

            _logger.LogError("Unable to get IHttpConnectionFeature.");
            return null;
        }
    }
}
