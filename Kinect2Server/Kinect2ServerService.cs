﻿using Microsoft.Kinect;
using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Windows.Media;

namespace PersonalRobotics.Kinect2Server
{
    /// <summary>
    /// A service that publishes data from the Kinect2 over TCP sockets.
    /// </summary>
    /// See: http://msdn.microsoft.com/en-us/library/system.serviceprocess.servicebase(v=vs.110).aspx
    /// 
    public class Kinect2ServerService : ServiceBase
    {
        KinectSensor kinect;
        MultiSourceFrameReader reader;
        AudioSource audioSource;
        AudioBeamFrameReader audioReader;

        AsyncNetworkConnector colorConnector;
        AsyncNetworkConnector depthConnector;
        AsyncNetworkConnector irConnector;
        AsyncNetworkConnector bodyIndexConnector;
        AsyncNetworkConnector audioConnector;

        byte[] colorArray;
        ushort[] depthArray;
        ushort[] irArray;
        byte[] byteDepthArray;
        byte[] byteIRArray;
        AudioContainer audioContainer;


        static readonly int BYTES_PER_COLOR_PIXEL = (PixelFormats.Bgr32.BitsPerPixel + 7) / 8;
        const int BYTES_PER_DEPTH_PIXEL = 2;
        const int BYTES_PER_IR_PIXEL = 2;

        public Kinect2ServerService()
        {
            this.ServiceName = "Kinect2Server";
            this.CanStop = true;
            this.CanPauseAndContinue = false;
            this.AutoLog = true;
        }

        /// <summary>
        /// Property that indicates whether the Kinect Server is connected to a sensor.
        /// </summary>
        public bool IsConnected { get { return (this.kinect != null) && kinect.IsAvailable; } }

        /// <summary>
        /// Event that triggers when the server detects a Kinect connection or disconnecting.
        /// </summary>
        public event EventHandler<IsConnectedChangedEventArgs> IsConnectedChanged;

        protected override void OnStart(string[] args)
        {
            // Try to open the first available Kinect sensor.
            this.kinect = KinectSensor.GetDefault();
            if (this.kinect == null)
            {
                EventLog.WriteEntry("No Kinect device was detected.");  
                ExitCode = -1;
                throw new KinectException("No kinect device was detected.");
            }
            else
            {
                this.kinect.Open();
                this.kinect.IsAvailableChanged += this.OnAvailableChanged;
            }

            // Register as a handler for the image data being returned by the Kinect.
            this.reader = this.kinect.OpenMultiSourceFrameReader(FrameSourceTypes.Color | FrameSourceTypes.Depth | FrameSourceTypes.Infrared);
            this.audioSource = this.kinect.AudioSource;
            if (this.reader == null)
            {
                EventLog.WriteEntry("Unable to connect to Kinect data stream.");
                ExitCode = -2;
                throw new KinectException("Unable to connect to Kinect data stream.");
            }
            else
            {
                this.reader.MultiSourceFrameArrived += this.OnFrameArrived;
            }
            if (this.audioSource == null)
            {
                EventLog.WriteEntry("Unable to open audio source on kinect");
                ExitCode = -3;
                throw new KinectException("Unable to connect to kinect audio source");
            }
            else
            {
                Console.WriteLine("Yay");
                this.audioReader = this.audioSource.OpenReader();
                if (this.audioReader == null)
                    Console.WriteLine("Issues with audio reader");
                this.audioReader.FrameArrived += this.onAudioFrameArrived;
            }


            // Allocate storage for the data from the Kinect.
            this.colorArray = new byte[this.kinect.ColorFrameSource.FrameDescription.Height * this.kinect.ColorFrameSource.FrameDescription.Width * BYTES_PER_COLOR_PIXEL];
            this.depthArray = new ushort[this.kinect.DepthFrameSource.FrameDescription.Height * this.kinect.DepthFrameSource.FrameDescription.Width];
            this.irArray = new ushort[this.kinect.InfraredFrameSource.FrameDescription.Height * this.kinect.InfraredFrameSource.FrameDescription.Width];
            this.byteDepthArray = new byte[this.kinect.DepthFrameSource.FrameDescription.Height * this.kinect.DepthFrameSource.FrameDescription.Width * BYTES_PER_DEPTH_PIXEL];
            this.byteIRArray = new byte[this.kinect.InfraredFrameSource.FrameDescription.Height * this.kinect.InfraredFrameSource.FrameDescription.Width * BYTES_PER_IR_PIXEL];
            this.audioContainer = new AudioContainer();
            this.audioContainer.samplingFrequency = 16000;
            this.audioContainer.frameLifeTime = 0.016;
            this.audioContainer.numSamplesPerFrame = (int)(this.audioContainer.samplingFrequency * this.audioContainer.frameLifeTime);
            this.audioContainer.numBytesPerSample = sizeof(float);


            // Create network connectors that will send out the data when it is received.
            this.colorConnector = new AsyncNetworkConnector(Properties.Settings.Default.RgbImagePort);
            this.depthConnector = new AsyncNetworkConnector(Properties.Settings.Default.DepthImagePort);
            this.irConnector = new AsyncNetworkConnector(Properties.Settings.Default.IrImagePort);
            this.audioConnector = new AsyncNetworkConnector(Properties.Settings.Default.AudioPort);

            // Open the server connections.
            this.colorConnector.Listen();
            this.depthConnector.Listen();
            this.irConnector.Listen();
            this.audioConnector.Listen();
        }

        protected override void OnStop()
        {
            this.kinect.Close();
            this.colorConnector.Close();
            this.depthConnector.Close();
            this.irConnector.Close();
            this.audioConnector.Close();

            this.reader.Dispose(); // TODO: Is this actually necessary?
            this.colorConnector.Dispose();
            this.depthConnector.Dispose();
            this.irConnector.Dispose();
            this.audioConnector.Dispose();
        }

        private void OnFrameArrived(object sender, MultiSourceFrameArrivedEventArgs e)
        {
            MultiSourceFrame multiSourceFrame = e.FrameReference.AcquireFrame();
            
            using (ColorFrame colorFrame = multiSourceFrame.ColorFrameReference.AcquireFrame())
            {
                if (colorFrame != null)
                {
                    colorFrame.CopyConvertedFrameDataToArray(this.colorArray, ColorImageFormat.Bgra);
                    this.colorConnector.Broadcast(this.colorArray);
                }
            }

            using (DepthFrame depthFrame = multiSourceFrame.DepthFrameReference.AcquireFrame())
            {
                if (depthFrame != null)
                {
                    depthFrame.CopyFrameDataToArray(this.depthArray);
                    System.Buffer.BlockCopy(this.depthArray, 0, this.byteDepthArray, 0, this.byteDepthArray.Length);
                    this.depthConnector.Broadcast(this.byteDepthArray);
                }
            }

            using (InfraredFrame irFrame = multiSourceFrame.InfraredFrameReference.AcquireFrame())
            {
                if (irFrame != null)
                {
                    irFrame.CopyFrameDataToArray(this.irArray);
                    System.Buffer.BlockCopy(this.irArray, 0, this.byteIRArray, 0, this.byteIRArray.Length);
                    this.irConnector.Broadcast(this.byteIRArray);
                }
            }
        }

        private void onAudioFrameArrived(object sender,AudioBeamFrameArrivedEventArgs e)
        {
            AudioBeamFrameReference audioFrameRefrence = e.FrameReference;
            try
            {
                Console.WriteLine("Oye");
                AudioBeamFrameList frameList = audioFrameRefrence.AcquireBeamFrames();
                if (frameList != null)
                {
                    Console.WriteLine("Yo, Thre was a audio event");
                }
            }
            catch
            {

            }
        }

        protected void OnAvailableChanged(object sender, IsAvailableChangedEventArgs e)
        {
            this.IsConnectedChanged(this, new IsConnectedChangedEventArgs(e.IsAvailable));
        }
    }

    /// <summary>
    /// An exception indicating that a Kinect was not detected.
    /// </summary>
    public class KinectException : Exception
    {
        public KinectException()
        {
        }

        public KinectException(string message)
            : base(message)
        {
        }

        public KinectException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Event triggered where the server connects or disconnects from a Kinect.
    /// </summary>
    public class IsConnectedChangedEventArgs : EventArgs
    {
        bool isConnected;
        public IsConnectedChangedEventArgs(bool isConnected)
        {
            this.isConnected = isConnected;
        }

        public bool IsConnected { get { return isConnected; } }
    }
}
