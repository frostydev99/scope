﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DGScope.Library;
using DGScope.Receivers;
using libmetar;
using Newtonsoft.Json;

namespace DGScope.Receivers.ScopeServer
{
    public class ScopeServerClient : Receiver
    {
        private Dictionary<Guid, Guid> associatedFlightPlans = new Dictionary<Guid, Guid>();
        private List<Track> tracks = new List<Track>();
        private List<FlightPlan> flightPlans = new List<FlightPlan>();
        private UpdateConverter updateConverter = new UpdateConverter();
        private bool stop = true;
        private bool running = false;
        public string Url { get; set; }
        public string Username { get; set; }
        [PasswordPropertyText(true)]
        public string Password { get; set; }
        public override void Start()
        {
            if (running)
                return;
            running = true;
            stop = false;
            Task.Run(Receive);
        }

        private bool streamended = true;
        private static async Task<string> ReadString(ClientWebSocket ws)
        {
            ArraySegment<byte> buffer = new ArraySegment<byte>(new byte[49316]);

            WebSocketReceiveResult result = null;
            using (var ms = new MemoryStream())
            {
                do
                {
                    result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                    ms.Write(buffer.Array, buffer.Offset, result.Count);
                }
                while (!result.EndOfMessage);
                ms.Seek(0, SeekOrigin.Begin);
                using (var reader = new StreamReader(ms, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
        }

        private async Task ProcessLine(string line)
        {
            JsonUpdate obj = JsonConvert.DeserializeObject<JsonUpdate>(line);
            Update update;
            switch (obj.UpdateType)
            {
                case 0:
                    update = JsonConvert.DeserializeObject<TrackUpdate>(line);
                    ProcessUpdate(update);
                    break;
                case 1:
                    update = JsonConvert.DeserializeObject<FlightPlanUpdate>(line);
                    ProcessUpdate(update);
                    break;
            }
        }
        private async Task<bool> Receive()
        {
            /// Try some websocket
            Uri uri = new Uri(Url);
            NetworkCredential credentials = new NetworkCredential(Username, Password);
            
            var scheme = uri.GetLeftPart(UriPartial.Scheme);
            switch (scheme.ToLower())
            {
                case "wss://":
                case "ws://":
                    using (var client = new ClientWebSocket())
                    {
                        var cts = new CancellationTokenSource();
                        client.Options.Credentials = credentials;
                        client.Options.KeepAliveInterval = TimeSpan.FromMinutes(30);
                        client.ConnectAsync(uri, cts.Token);
                        while (client.State == WebSocketState.Connecting)
                        {
                            Thread.Sleep(1000);
                        }
                        Debug.WriteLine("connected!");
                        while (client.State == WebSocketState.Open)
                        {
                            Debug.WriteLine("reading a line");
                            
                            var line = await ReadString(client);
                            ProcessLine(line);
                        }
                    }
                    break;
                case "http://":
                case "https://":
                    using (var client = new WebClient())
                    {
                        client.Credentials = new NetworkCredential(Username, Password);
                        client.OpenReadCompleted += (sender, e) =>
                        {
                            if (e.Error == null)
                            {
                                using (var reader = new StreamReader(e.Result))
                                {
                                    while (!stop)
                                    {
                                        try
                                        {
                                            var line = reader.ReadLine();
                                            if (line == null)
                                                continue;
                                            ProcessLine(line);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.WriteLine(ex.Message);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine(e.Error.ToString());
                            }
                            streamended = true;
                        };

                        while (!stop)
                        {
                            streamended = false;
                            client.OpenReadAsync(new Uri(Url));
                            while (!streamended)
                            {
                                System.Threading.Thread.Sleep(1000);
                            }
                        }
                    }
                    break;
            }
            running = false;

            if (stop)
                return true;
            return false;
                
        }
        public async Task ProcessUpdate(Update update)
        {
            Aircraft plane = null; ;
            Guid updateGuid = update.Guid;
            Track track = null;
            FlightPlan flightPlan = null;
            switch (update.UpdateType)
            {
                case UpdateType.Track:
                    lock (tracks)
                    {
                        track = tracks.Where(x => x.Guid == updateGuid).FirstOrDefault();
                        if (track == null)
                        {
                            track = new Track(updateGuid);
                            tracks.Add(track);
                        }
                    }
                    track.UpdateTrack(update as TrackUpdate);
                    flightPlan = flightPlans.Where(x => x.AssociatedTrack == track).FirstOrDefault();
                    plane = GetPlane(updateGuid, true);
                    break;
                case UpdateType.Flightplan:
                    lock (flightPlans)
                    {
                        flightPlan = flightPlans.Where(x => x.Guid == updateGuid).FirstOrDefault();
                        if (flightPlan == null)
                        {
                            flightPlan = new FlightPlan(updateGuid);
                            flightPlans.Add(flightPlan);
                        }
                    }
                    flightPlan.UpdateFlightPlan(update as FlightPlanUpdate);
                    var associatedTrack = (update as FlightPlanUpdate).AssociatedTrackGuid;
                    if (associatedTrack != null)
                        flightPlan.AssociateTrack(tracks.Where(x => x.Guid == associatedTrack).FirstOrDefault());
                    if (flightPlan.AssociatedTrack != null)
                    {
                        plane = GetPlane(flightPlan.AssociatedTrack.Guid, false);
                        track = flightPlan.AssociatedTrack;
                    }
                    break;
            }
            if (plane == null)
                return;
            
            if (update.TimeStamp > plane.LastMessageTime)
                plane.LastMessageTime = update.TimeStamp;
            if (flightPlan != null)
            {
                plane.Type = flightPlan.AircraftType;
                plane.FlightPlanCallsign = flightPlan.Callsign;
                plane.Destination = flightPlan.Destination;
                plane.FlightRules = flightPlan.FlightRules;
                plane.Category = flightPlan.WakeCategory;
                plane.PositionInd = flightPlan.Owner;
                plane.PendingHandoff = flightPlan.PendingHandoff;
                plane.RequestedAltitude = flightPlan.RequestedAltitude;
                plane.Scratchpad = flightPlan.Scratchpad1;
                plane.Scratchpad2 = flightPlan.Scratchpad2;
                plane.LDRDirection = RadarWindow.ParseLDR(flightPlan.LDRDirection.ToString());
            }
            if (track != null)
            {
                if (track.Altitude != null)
                {
                    if (plane.Altitude != null)
                    {
                        plane.Altitude.Value = track.Altitude.Value;
                        plane.Altitude.AltitudeType = track.Altitude.AltitudeType;
                    }
                }
                plane.Callsign = track.Callsign;
                plane.GroundSpeed = track.GroundSpeed;
                if (track.PropertyUpdatedTimes.ContainsKey(track.GetType().GetProperty("GroundTrack")))
                    plane.SetTrack(track.GroundTrack, track.PropertyUpdatedTimes[track.GetType().GetProperty("GroundTrack")]);
                plane.Ident = track.Ident;
                plane.IsOnGround = track.IsOnGround;
                if (track.PropertyUpdatedTimes.ContainsKey(track.GetType().GetProperty("Location")))
                    plane.SetLocation(track.Location, track.PropertyUpdatedTimes[track.GetType().GetProperty("Location")]);
                plane.ModeSCode = track.ModeSCode;
                plane.Squawk = track.Squawk;
                plane.VerticalRate = track.VerticalRate;
            }
        }

        public override void Stop()
        {
            stop = true;
        }
    }
}
