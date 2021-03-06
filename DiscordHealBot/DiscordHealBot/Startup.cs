﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace DiscordHealBot
{
    public class Startup
    {
        
        public Startup(IConfiguration configuration, IServiceCollection serviceCollection)
        {
            ServiceProvider = serviceCollection.BuildServiceProvider();
            AssertConfiguration(configuration);
            if (Settings.StoreData)
            {
                ConnectionMultiplexer redis = ConnectionMultiplexer.Connect(Settings.ConnectionStrings);
                db = redis.GetDatabase();
            }
              
            using (var scope = ServiceProvider.CreateScope())
            {
                Logger = scope.ServiceProvider.GetService<ILogger<Startup>>();
            }

            this.QueueResults = new ConcurrentQueue<EndPointHealthResult>();
            this.QueueErrors = new ConcurrentQueue<EndPointHealthResult>();
            this.CancellationTokenSource = new CancellationTokenSource();
        }

        protected bool isFirstRun { get; set; }
        protected IDatabase db { get; }
        protected ILogger Logger { get; }
        protected ServiceProvider ServiceProvider { get; }
        protected CancellationTokenSource CancellationTokenSource { get; }

        protected JobSettings Settings { get; set; }
        protected List<Endpoint> Endpoints { get; set; }
        protected string DiscordWebHook { get; set; }

        protected ConcurrentQueue<EndPointHealthResult> QueueResults { get; set; }
        protected ConcurrentQueue<EndPointHealthResult> QueueErrors { get; set; }

        public async Task RunAsync()
        {
            Logger.LogInformation($"Job Started at {DateTime.UtcNow:h:mm:ss tt zz}, {Settings.TimeInterval}s interval");
            if(Settings.StoreData)
                InjectEndpointsResultsList();
            isFirstRun = true;
            Task pollingTask = PollAsync();
            Task repotingTask = ReportAsync();
            await Task.WhenAll(pollingTask, repotingTask);
        }

        private void InjectEndpointsResultsList()
        {
            try
            {
                if (db.KeyExists("endPointsResultsList"))
                {
                    List<EndPointHealthResult> list = JsonSerializer.Deserialize<List<EndPointHealthResult>>(db.StringGet("endPointsResultsList"));
                    foreach (var element in list)
                    {
                        QueueResults.Enqueue(element);
                    }
                }
                db.KeyDelete("endPointsResultsList");
            }
            catch(Exception e)
            {
                Logger.LogInformation("Redis connexion failed. It might be a network issue.");
            }
        }

        private async Task ReportAsync()
        {
            while (!CancellationTokenSource.IsCancellationRequested)
            {
                Logger.LogInformation($"Job is announcing at {DateTime.UtcNow:h:mm:ss tt zz} ..." + QueueResults.Count());
                List<EndPointHealthResult> epResults = new List<EndPointHealthResult>();
                while (QueueResults.TryDequeue(out var endPointHealthResult))
                {
                    epResults.Add(endPointHealthResult);
                }

                if (epResults.Count > 0)
                {
                    await BroadCaster.BroadcastResultsAsync(epResults, this.DiscordWebHook, Logger, Settings.FamilyReporting);
                    if (Settings.StoreData)
                        CleanRedis();
                } else
                {
                    if(!isFirstRun)
                        await BroadCaster.BroadcastErrorAsync(this.DiscordWebHook, Logger);
                }
                
                TimeSpan delay;
                if(Settings.FixedTime)
                {
                    switch (Settings.TimeUnit)
                    {
                        case "day" :
                            delay = (DateTime.Now.AddDays(1).Date - DateTime.Now);
                            break;
                        default:
                        case "hour" :
                            delay = TimeSpan.FromMinutes(60 - DateTime.Now.Minute);
                            break;
                        case "minute":
                            delay = TimeSpan.FromSeconds(60 - DateTime.Now.Second);
                            break;
                    }
                } else
                {
                    delay = TimeSpan.FromMilliseconds(Settings.GetAnnouncementTimeIntervalInMs());
                }
                isFirstRun = false;
                await Task.Delay(delay);
            }
        }

        private void CleanRedis()
        {
            try
            {
                db.KeyDelete("endPointsResultsList");
            }
            catch (Exception e)
            {
                Logger.LogInformation("Redis connexion failed. It might be a network issue.");
            }
        }

        private async Task PollAsync()
        {
            while (!CancellationTokenSource.IsCancellationRequested)
            {
                Logger.LogInformation("Job is polling ..." + QueueResults.Count());
                List<EndPointHealthResult> epResults = await RunEndPointsAsync();
                foreach (EndPointHealthResult endPointHealthResult in epResults)
                {
                    if(Settings.SendAlert && endPointHealthResult.Latency > Settings.AlertFloor)
                        QueueErrors.Enqueue(endPointHealthResult);
                    else
                        QueueResults.Enqueue(endPointHealthResult);
                }

                await TrySendAlertAsync();

                if (Settings.StoreData)
                    StoreEndpointsList(QueueResults.ToList());

                await Task.Delay(Settings.GetPollingTimeIntervalInMs());
            }
        }

        private async Task TrySendAlertAsync()
        {
            if (Settings.SendAlert)
            {
                if (QueueErrors.Count > 0)
                {
                    List<EndPointHealthResult> exceeded = new List<EndPointHealthResult>();
                    while (QueueErrors.TryDequeue(out var endPointHealthResult))
                    {
                        exceeded.Add(endPointHealthResult);
                    }

                    bool success = await BroadCaster.BroadcastAlertAsync(exceeded, DiscordWebHook, Logger);
                    if (success)
                    {
                        foreach (var e in exceeded)
                        {
                            QueueResults.Enqueue(e);
                        }
                    }
                }
            }
        }

        private async Task<List<EndPointHealthResult>> RunEndPointsAsync()
        {
            List<EndPointHealthResult> endPointHealthResults = new List<EndPointHealthResult>();
            foreach (Endpoint ep in Endpoints)
            {
                Stopwatch stopwatch = new Stopwatch();
                bool success = false;
                int statusCode = 0;
                stopwatch.Start();
                try
                {
                    var response = await ep.Address.GetAsync();
                    success = response.StatusCode > 199 && response.StatusCode < 400;
                    statusCode = response.StatusCode;
                }
                catch (Exception e)
                {
                    Logger.LogInformation(e.Message);
                }

                stopwatch.Stop();

                EndPointHealthResult healthResult = new EndPointHealthResult()
                {
                    Latency = stopwatch.ElapsedMilliseconds,
                    EndpointAddress = ep.Address,
                    Success = success,
                    StatusCode = statusCode,
                    Family = ep.FamilyName,
                    DateRun = DateTime.UtcNow
                };

                endPointHealthResults.Add(healthResult);
                
            }
           
            return endPointHealthResults.ToList();
        }

        private void StoreEndpointsList(List<EndPointHealthResult> list)
        {
            try
            {
                db.StringSet("endPointsResultsList", $"{JsonSerializer.Serialize(list)}");
            }
            catch (Exception e)
            {
                
                Logger.LogInformation("Redis connexion failed. It might be a network issue.");
            }
        }

        protected void AssertConfiguration(IConfiguration configuration)
        {
            JobSettings settings = configuration.GetSection("JobSettings").Get<JobSettings>();
            if (settings == null || settings.TimeInterval < 1 || settings.PollingInterval < 1)
            {
                throw new InvalidDataException("JobParameters missing from appsettings or incorrect values");
            }

            if (settings.FixedTime && string.IsNullOrWhiteSpace(settings.TimeUnit))
            {
                throw new InvalidDataException("No Time unit, check your appsettings file");
            }

            if (settings.StoreData && string.IsNullOrWhiteSpace(settings.ConnectionStrings))
            {
                throw new InvalidDataException("No ConnectionStrings for redis storage, check your appsettings file");
            }

            if (settings.SendAlert && settings.AlertFloor < 1)
            {
                throw new InvalidDataException("Alert floor missing or incorrect value, check your appsettings file");
            }

            Settings = settings;

            List<Endpoint> endpoints = configuration.GetSection("Endpoints").Get<List<Endpoint>>();
            if (endpoints == null || endpoints.Count < 1)
            {
                throw new InvalidDataException("No Endpoint to monitor, check your appsettings file");
            }

            Endpoints = endpoints;

            string discordWebHook = configuration.GetSection("DiscordWebhook").Get<string>();
            if (string.IsNullOrWhiteSpace(discordWebHook))
            {
                throw new InvalidDataException("No Discord Endpoint, check your appsettings file");
            }

            DiscordWebHook = discordWebHook;
        }

    }
}