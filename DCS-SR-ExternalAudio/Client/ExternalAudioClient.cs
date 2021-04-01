﻿using System;
using System.Net;
using System.Threading;
using Ciribob.DCS.SimpleRadio.Standalone.Common;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Network;
using Ciribob.DCS.SimpleRadio.Standalone.ExternalAudioClient.Audio;
using Ciribob.DCS.SimpleRadio.Standalone.ExternalAudioClient.Models;
using Ciribob.DCS.SimpleRadio.Standalone.ExternalAudioClient.Network;
using Easy.MessageHub;
using NLog;
using Timer = Cabhishek.Timers.Timer;

namespace Ciribob.DCS.SimpleRadio.Standalone.ExternalAudioClient.Client
{
    internal class ExternalAudioClient
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private string mp3Path;
        private double[] freq;
        private RadioInformation.Modulation[] modulation;
        private byte[] modulationBytes;
        private int coalition;
        private readonly int port;

        private readonly string Guid = ShortGuid.NewGuid();

        private CancellationTokenSource finished = new CancellationTokenSource();
        private DCSPlayerRadioInfo gameState;
        private UdpVoiceHandler udpVoiceHandler;
        private string name;
        private readonly float volume;

        private readonly string SpeakerGender;
        private readonly string SpeakerCulture;

        private string ipAddress = "127.0.0.1";


        public ExternalAudioClient(string mp3Path, double[] freq, RadioInformation.Modulation[] modulation, int coalition, int port, string name, float volume, string SpeakerGender, string SpeakerCulture)
        {
            this.mp3Path = mp3Path;
            this.freq = freq;
            this.modulation = modulation;
            this.coalition = coalition;
            this.port = port;
            this.name = name;
            this.volume = volume;
            this.SpeakerGender = SpeakerGender;
            this.SpeakerCulture = SpeakerCulture;

            this.modulationBytes = new byte[modulation.Length];
            for (int i = 0; i < modulationBytes.Length; i++)
            {
                modulationBytes[i] = (byte)modulation[i];
            }

        }

        public void Start()
        {

            MessageHub.Instance.Subscribe<ReadyMessage>(ReadyToSend);
            MessageHub.Instance.Subscribe<DisconnectedMessage>(Disconnected);

            gameState = new DCSPlayerRadioInfo();
            gameState.radios[1].modulation = modulation[0];
            gameState.radios[1].freq = freq[0]; // get into Hz
            gameState.radios[1].name = name;

            Logger.Info($"Starting with params:");
            Logger.Info($"Path or Text to Say: {mp3Path} ");
            for (int i = 0; i < freq.Length; i++)
            {
                Logger.Info($"Frequency: {freq[i]} Hz - {modulation[i]} ");
            }
            Logger.Info($"Coalition: {coalition} ");
            Logger.Info($"IP: {ipAddress} ");
            Logger.Info($"Port: {port} ");
            Logger.Info($"Client Name: {name} ");
            Logger.Info($"Volume: {volume} ");
            Logger.Info($"Voice: {SpeakerGender}|{SpeakerCulture} ");


            var srsClientSyncHandler = new SRSClientSyncHandler(Guid, gameState,name, coalition);

            srsClientSyncHandler.TryConnect(new IPEndPoint(IPAddress.Parse(ipAddress), port));

            //wait for it to end
            finished.Token.WaitHandle.WaitOne();
            Logger.Info("Finished - Closing");

            udpVoiceHandler?.RequestStop();
            srsClientSyncHandler?.Disconnect();

            MessageHub.Instance.ClearSubscriptions();
        }

        private void ReadyToSend(ReadyMessage ready)
        {
            if (udpVoiceHandler == null)
            {
                Logger.Info($"Connecting UDP VoIP");
                udpVoiceHandler = new UdpVoiceHandler(Guid, IPAddress.Parse(ipAddress), port, gameState);
                udpVoiceHandler.Start();
                new Thread(SendAudio).Start();
            }
        }

        private void Disconnected(DisconnectedMessage disconnected)
        {
            finished.Cancel();
        }

        private void SendAudio()
        {
            Logger.Info("Sending Audio... Please Wait");
            AudioGenerator mp3 = new AudioGenerator(mp3Path, volume, SpeakerGender, SpeakerCulture);
            var opusBytes = mp3.GetOpusBytes();
            int count = 0;

            CancellationTokenSource tokenSource = new CancellationTokenSource();

            //get all the audio as Opus frames of 40 ms
            //send on 40 ms timer 

            //when empty - disconnect
            //user timer for accurate sending
            var _timer = new Timer(() =>
            {

                if (!finished.IsCancellationRequested)
                {
                    if (count < opusBytes.Count)
                    {
                        udpVoiceHandler.Send(opusBytes[count], opusBytes[count].Length, freq, modulationBytes);
                        count++;

                        if (count % 50 == 0)
                        {
                            Logger.Info($"Playing audio - sent {count * 40}ms - {((float)count / (float)opusBytes.Count) * 100.0:F0}% ");
                        }
                    }
                    else
                    {
                        tokenSource.Cancel();
                    }
                }
                else
                {
                    Logger.Error("Client Disconnected");
                    tokenSource.Cancel();
                    return;
                }

            }, TimeSpan.FromMilliseconds(40));
            _timer.Start();

            //wait for cancel
            tokenSource.Token.WaitHandle.WaitOne();
            _timer.Stop();

            Logger.Info("Finished Sending Audio");
            finished.Cancel();
        }
    }
}
