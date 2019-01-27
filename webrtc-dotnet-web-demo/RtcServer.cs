﻿using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.WebSockets;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using webrtc_dotnet_standard;

namespace webrtc_dotnet_demo
{
    public static class RtcServer
    {
        private static void VideoRenderer(object parameter)
        {
            try
            {
                var pc = (ObservablePeerConnection) parameter;

                using (var background = Image.Load("background-small.jpg"))
                {
                    var frame = background.Frames[0];

                    TimeSpan startTime = TimeSpan.Zero;
                    TimeSpan nextFrameTime = TimeSpan.Zero;
                    TimeSpan frameDuration = TimeSpan.FromSeconds(1.0 / 60);

                    while (Thread.CurrentThread.IsAlive && !pc.IsDisposed)
                    {
                        if (pc.SignalingState == SignalingState.Stable)
                        {
                            var currentTime = SimplePeerConnection.GetRealtimeClockTimeInMicroseconds();

                            if (startTime == TimeSpan.Zero)
                            {
                                startTime = currentTime;
                            }

                            if (currentTime >= nextFrameTime)
                            {
                                var pixels = MemoryMarshal.Cast<Rgba32, uint>(frame.GetPixelSpan());
                                pc.SendVideoFrame(
                                    MemoryMarshal.GetReference(pixels), 
                                    frame.Width * 4, 
                                    frame.Width,
                                    frame.Height,
                                    PixelFormat.Rgba32);

                                // TODO: Use Math.DivRem and take remainder into account?
                                // TODO: Should get feedback from connected peer about frame-rate and resolution.
                                var frameIndex = (currentTime.Ticks - startTime.Ticks) / frameDuration.Ticks;
                                nextFrameTime = startTime + (frameIndex + 1) * frameDuration;
                            }
                            else
                            {
                                // TODO: Use Win32 waitable timers, or expose webrtc's high-precision (?) TaskQueue
                                Thread.Sleep(0);
                            }
                        }
                        else
                        {
                            // Wait until peer connection is stable before sending frames.
                            Thread.Sleep(500);
                        }
                    }
                }
            }

            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public static async Task Run(WebSocket ws)
        {
            var receiveBuffer = new byte[1024 * 4];
            var renderThread = new Thread(VideoRenderer);

            using (var pc = new ObservablePeerConnection("server", options => { }))
            using (pc.LocalIceCandidateStream.Subscribe(ice => ws.SendJsonAsync("ice", ice)))
            using (pc.LocalSessionDescriptionStream.Subscribe(sd => ws.SendJsonAsync("sdp", sd)))
            using (pc.SignalingStateStream.Subscribe(ss =>
            {
                if (renderThread.ThreadState == ThreadState.Unstarted && ss == SignalingState.Stable)
                {
                    renderThread.Start(pc);
                }
            }))
            {

                var msgStream = Observable.Never<DataMessage>();
                var iceStream = new Subject<IceCandidate>();
                var sdpStream = new Subject<SessionDescription>();

                pc.Connect(msgStream, sdpStream, iceStream);

                pc.AddDataChannel("data", DataChannelFlag.None);
                pc.AddStream(StreamTrack.Video);

                pc.CreateOffer();


                while (!ws.CloseStatus.HasValue)
                {
                    var message = await ws.ReceiveJsonAsync(default, receiveBuffer);
                    if (message == null)
                        break;

                    var payload = message["payload"];

                    if (payload.Any())
                    {
                        switch (message["action"].Value<string>())
                        {
                            case "ice":
                            {
                                iceStream.OnNext(new IceCandidate(payload));
                                break;
                            }

                            case "sdp":
                            {
                                sdpStream.OnNext(new SessionDescription(payload));
                                break;
                            }
                        }
                    }
                }

                Console.WriteLine("Websocket was closed by client");

                renderThread.Interrupt();
                renderThread.Join();
            }
        }
    }
}
