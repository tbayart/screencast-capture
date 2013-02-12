﻿// Screencast Capture, free screen recorder
// http://screencast-capture.googlecode.com
//
// Copyright © César Souza, 2012-2013
// cesarsouza at gmail.com
//
//    This program is free software; you can redistribute it and/or modify
//    it under the terms of the GNU General Public License as published by
//    the Free Software Foundation; either version 2 of the License, or
//    (at your option) any later version.
//
//    This program is distributed in the hope that it will be useful,
//    but WITHOUT ANY WARRANTY; without even the implied warranty of
//    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//    GNU General Public License for more details.
//
//    You should have received a copy of the GNU General Public License
//    along with this program; if not, write to the Free Software
//    Foundation, Inc., 675 Mass Ave, Cambridge, MA 02139, USA.
// 

namespace ScreenCapture.ViewModels
{
    using Accord.Audio;
    using Accord.DirectSound;
    using AForge.Controls;
    using AForge.Imaging.Filters;
    using AForge.Video;
    using AForge.Video.FFMPEG;
    using ScreenCapture.Native;
    using ScreenCapture.Processors;
    using ScreenCapture.Properties;
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.ComponentModel;
    using System.Drawing;
    using System.Drawing.Drawing2D;
    using System.IO;
    using System.Windows.Forms;

    /// <summary>
    ///   Region capturing modes.
    /// </summary>
    /// 
    public enum CaptureRegionOption
    {
        /// <summary>
        ///   Captures from a fixed region on the screen.
        /// </summary>
        /// 
        Fixed,

        /// <summary>
        ///   Captures only from the primary screen.
        /// </summary>
        /// 
        Primary,

        /// <summary>
        ///   Captures from the current window.
        /// </summary>
        Window
    }

    /// <summary>
    ///   Main ViewModel to control the application.
    /// </summary>
    /// 
    public class RecorderViewModel : INotifyPropertyChanged, IDisposable
    {

        private MainViewModel main;

        private CaptureRegionOption captureMode;
        private ScreenCaptureStream screenStream;
        private VideoFileWriter videoWriter;
        private VideoSourcePlayer videoPlayer;

        private AudioCaptureDevice audioDevice;

        private Crop crop = new Crop(Rectangle.Empty);
        private CaptureCursor cursorCapture;
        private CaptureClick clickCapture;
        private CaptureKeyboard keyCapture;
        private Object syncObj = new Object();


        /// <summary>
        ///   Gets the path to the output file generated by
        ///   the recorder. This file is the recorded video.
        /// </summary>
        /// 
        public string OutputPath { get; private set; }


        /// <summary>
        ///   Gets or sets the current capture mode, if the capture area
        ///   should be the whole screen, a fixed region or a fixed window.
        /// </summary>
        /// 
        public CaptureRegionOption CaptureMode
        {
            get { return captureMode; }
            set { onCaptureModeChanged(value); }
        }

        /// <summary>
        ///   Gets or sets the current capture region.
        /// </summary>
        /// 
        public Rectangle CaptureRegion { get; set; }

        /// <summary>
        ///   Gets or sets the current capture window.
        /// </summary>
        /// 
        public IWin32Window CaptureWindow { get; set; }

        /// <summary>
        ///   Gets the initial recording time.
        /// </summary>
        /// 
        public DateTime RecordingStartTime { get; private set; }

        /// <summary>
        ///   Gets the current recording time.
        /// </summary>
        /// 
        public TimeSpan RecordingDuration { get; private set; }

        /// <summary>
        ///   Gets whether the view-model is waiting for the
        ///   user to select a target window to be recorded.
        /// </summary>
        /// 
        public bool IsWaitingForTargetWindow { get; private set; }

        /// <summary>
        ///   Gets whether the application is recording the screen.
        /// </summary>
        /// 
        public bool IsRecording { get; private set; }

        /// <summary>
        ///   Gets whether the application is grabbing frames from the screen.
        /// </summary>
        /// 
        public bool IsPlaying { get; private set; }

        /// <summary>
        ///   Gets whether the application has already finished recording a video.
        /// </summary>
        /// 
        public bool HasRecorded { get; private set; }

        /// <summary>
        ///   Gets whether the capture region frame should be visible.
        /// </summary>
        /// 
        public bool IsCaptureFrameVisible { get { return IsPlaying && CaptureMode == CaptureRegionOption.Fixed; } }

        /// <summary>
        ///   Gets or sets the current capture audio device. If set
        ///   to null, audio capturing will be disabled.
        /// </summary>
        /// 
        public AudioDeviceInfo CaptureAudioDevice { get; set; }

        /// <summary>
        ///   Gets a list of audio devices available in the system.
        /// </summary>
        /// 
        public static ReadOnlyCollection<AudioDeviceInfo> AudioDevices { get; private set; }


        /// <summary>
        ///   Occurs when the view-model needs a window to be recorded.
        /// </summary>
        /// 
        public event EventHandler ShowTargetWindow;





        /// <summary>
        ///   Initializes a new instance of the <see cref="MainViewModel"/> class.
        /// </summary>
        /// 
        public RecorderViewModel(MainViewModel main, VideoSourcePlayer player)
        {
            if (main == null)
                throw new ArgumentNullException("main");

            if (player == null)
                throw new ArgumentNullException("player");

            this.main = main;
            this.videoPlayer = player;
            this.videoPlayer.NewFrame += Player_NewFrame;

            this.CaptureMode = CaptureRegionOption.Primary;
            this.CaptureRegion = new Rectangle(0, 0, 640, 480);

            this.clickCapture = new CaptureClick();
            this.cursorCapture = new CaptureCursor();
            this.keyCapture = new CaptureKeyboard();

            if (Settings.Default.CaptureAudio)
                this.CaptureAudioDevice = new AudioDeviceCollection(AudioDeviceCategory.Capture).Default;
        }

        static RecorderViewModel()
        {
            AudioDevices = new ReadOnlyCollection<AudioDeviceInfo>(
                new List<AudioDeviceInfo>(new AudioDeviceCollection(AudioDeviceCategory.Capture)));
        }



        /// <summary>
        ///   Starts playing the preview screen, grabbing
        ///   frames, but not recording to a video file.
        /// </summary>
        /// 
        public void StartPlaying()
        {
            if (IsPlaying) return;

            // Checks if we were already waiting for a window
            // to be selected, in case the user had chosen to 
            // capture from a fixed window.

            if (IsWaitingForTargetWindow)
            {
                // Yes, we were. We will not be waiting anymore
                // since the user should have selected one now.
                IsWaitingForTargetWindow = false;
            }

            else
            {
                // No, this is the first time the user starts the
                // frame grabber. Let's check what the user wants

                if (CaptureMode == CaptureRegionOption.Window)
                {
                    // The user wants to capture from a window. So we
                    // need to ask which window we have to keep a look.

                    // We will return here and wait the user to respond; 
                    // when he finishes selecting he should signal back
                    // by calling SelectWindowUnderCursor().
                    IsWaitingForTargetWindow = true;
                    onTargetWindowRequested(); return;
                }
            }

            // All is well. Keep configuring and start
            CaptureRegion = Screen.PrimaryScreen.Bounds;

            double framerate = Settings.Default.FrameRate;
            int interval = (int)Math.Round(1000 / framerate);
            int height = CaptureRegion.Height;
            int width = CaptureRegion.Width;

            clickCapture.Enabled = true;
            keyCapture.Enabled = true;

            screenStream = new ScreenCaptureStream(CaptureRegion, interval);
            screenStream.VideoSourceError += screenStream_VideoSourceError;

            videoPlayer.VideoSource = screenStream;
            videoPlayer.Start();

            IsPlaying = true;
        }

        /// <summary>
        ///   Pauses the frame grabber, but keeps recording
        ///   if the software has already started recording.
        /// </summary>
        /// 
        public void PausePlaying()
        {
            if (!IsPlaying) return;

            videoPlayer.SignalToStop();
            IsPlaying = false;
        }



        /// <summary>
        ///   Starts recording. Only works if the player has
        ///   already been started and is grabbing frames.
        /// </summary>
        /// 
        public void StartRecording()
        {
            if (IsRecording || !IsPlaying) return;

            Rectangle area = CaptureRegion;
            string fileName = newFileName();

            int height = area.Height;
            int width = area.Width;
            int framerate = 1000 / screenStream.FrameInterval;
            int videoBitRate = 10 * 1000 * 1000;
            int audioBitRate = 320 * 1000;

            OutputPath = Path.Combine(main.CurrentDirectory, fileName);
            RecordingStartTime = DateTime.MinValue;
            videoWriter = new VideoFileWriter();

            if (CaptureAudioDevice != null)
            {
                audioDevice = new AudioCaptureDevice(CaptureAudioDevice.Guid);
                audioDevice.Format = SampleFormat.Format16Bit;
                audioDevice.SampleRate = Settings.Default.SampleRate;
                audioDevice.DesiredFrameSize = 4096;
                audioDevice.NewFrame += audioDevice_NewFrame;
                audioDevice.Start();

                videoWriter.Open(OutputPath, width, height, framerate, VideoCodec.H264, videoBitRate,
                    AudioCodec.MP3, audioBitRate, audioDevice.SampleRate, 1);
            }
            else
            {
                videoWriter.Open(OutputPath, width, height, framerate, VideoCodec.H264, videoBitRate);
            }

            HasRecorded = false;
            IsRecording = true;
        }

        /// <summary>
        ///   Stops recording.
        /// </summary>
        /// 
        public void StopRecording()
        {
            if (!IsRecording) return;

            lock (syncObj)
            {
                if (videoWriter != null)
                {
                    videoWriter.Close();
                    videoWriter.Dispose();
                    videoWriter = null;
                }

                if (audioDevice != null)
                {
                    audioDevice.Stop();
                    audioDevice.Dispose();
                    audioDevice = null;
                }

                IsRecording = false;
                HasRecorded = true;
            }
        }




        /// <summary>
        ///   Grabs the handle of the window currently under
        ///   the cursor, and if the application is waiting
        ///   for a handle, immediately starts playing.
        /// </summary>
        /// 
        public void SelectWindowUnderCursor()
        {
            CaptureWindow = SafeNativeMethods.WindowFromPoint(Cursor.Position);

            if (IsWaitingForTargetWindow)
                StartPlaying();
        }

        /// <summary>
        ///   Releases resources and prepares
        ///   the application for closing.
        /// </summary>
        /// 
        public void Close()
        {
            if (videoPlayer != null && videoPlayer.IsRunning)
            {
                videoPlayer.SignalToStop();
                videoPlayer.WaitForStop();
            }

            if (audioDevice != null && audioDevice.IsRunning)
            {
                audioDevice.SignalToStop();
                audioDevice.WaitForStop();
            }

            if (videoWriter != null && videoWriter.IsOpen)
                videoWriter.Close();
        }



        /// <summary>
        ///   Raises a property changed on <see cref="CaptureMode"/>.
        /// </summary>
        /// 
        private void onCaptureModeChanged(CaptureRegionOption value)
        {
            if (IsRecording)
                return;

            captureMode = value;

            if (value == CaptureRegionOption.Window && IsPlaying)
            {
                IsWaitingForTargetWindow = true;
                onTargetWindowRequested();
                IsWaitingForTargetWindow = false;
            }

            if (PropertyChanged != null)
                PropertyChanged(this, new PropertyChangedEventArgs("CaptureMode"));
        }

        /// <summary>
        ///   Raises the <see cref="ShowTargetWindow"/> event.
        /// </summary>
        /// 
        private void onTargetWindowRequested()
        {
            if (ShowTargetWindow != null)
                ShowTargetWindow(this, EventArgs.Empty);
        }


        private void Player_NewFrame(object sender, ref Bitmap image)
        {
            // Adjust the window according to the current capture
            // mode. Also adjusts to keep even widths and heights.
            CaptureRegion = adjustWindow();

            // Crop the image if the mode requires it
            if (CaptureMode == CaptureRegionOption.Fixed ||
                CaptureMode == CaptureRegionOption.Window)
            {
                crop.Rectangle = CaptureRegion;
                using (Bitmap oldImage = image)
                {
                    image = crop.Apply(oldImage);
                }
            }

            // Draw extra information on the screen
            bool captureMouse = Settings.Default.CaptureMouse;
            bool captureClick = Settings.Default.CaptureClick;
            bool captureKeys = Settings.Default.CaptureKeys;

            if (captureMouse || captureClick || captureKeys)
            {
                cursorCapture.CaptureRegion = CaptureRegion;
                clickCapture.CaptureRegion = CaptureRegion;
                keyCapture.Font = Settings.Default.KeyboardFont;

                using (Graphics g = Graphics.FromImage(image))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.SmoothingMode = SmoothingMode.HighQuality;

                    if (captureMouse)
                        cursorCapture.Draw(g);

                    if (captureClick)
                        clickCapture.Draw(g);

                    if (captureKeys)
                        keyCapture.Draw(g);
                }
            }


            lock (syncObj) // Save the frame to the video file.
            {
                if (IsRecording)
                {
                    if (RecordingStartTime == DateTime.MinValue)
                        RecordingStartTime = DateTime.Now;

                    RecordingDuration = DateTime.Now - RecordingStartTime;
                    videoWriter.WriteVideoFrame(image, RecordingDuration);
                }
            }
        }

        private void audioDevice_NewFrame(object sender, Accord.Audio.NewFrameEventArgs e)
        {
            lock (syncObj) // Save the frame to the video file.
            {
                if (IsRecording)
                {
                    videoWriter.WriteAudioFrame(e.Signal.RawData);
                }
            }
        }

        private void screenStream_VideoSourceError(object sender, VideoSourceErrorEventArgs eventArgs)
        {
            throw new VideoException(eventArgs.Description);
        }

        private Rectangle adjustWindow()
        {
            Rectangle area = CaptureRegion;

            if (CaptureMode == CaptureRegionOption.Window && !IsRecording)
            {
                if (!SafeNativeMethods.TryGetWindowRect(CaptureWindow, out area))
                    area = CaptureRegion;
            }
            else if (CaptureMode == CaptureRegionOption.Primary)
            {
                area = Screen.PrimaryScreen.Bounds;
            }

            if (area.Width % 2 != 0)
                area.Width++;
            if (area.Height % 2 != 0)
                area.Height++;

            return area;
        }

        private string newFileName()
        {
            string date = DateTime.Now.ToString("yyyy-MM-dd-HH'h'mm'm'ss's'",
                System.Globalization.CultureInfo.CurrentCulture);

            string mode = String.Empty;
            if (CaptureMode == CaptureRegionOption.Primary)
                mode = "Screen_";
            else if (CaptureMode == CaptureRegionOption.Fixed)
                mode = "Region_";
            else if (CaptureMode == CaptureRegionOption.Window)
                mode = "Window_";

            string name = mode + date + "." + Settings.Default.Container;

            return name;
        }


        #region IDisposable implementation

        /// <summary>
        ///   Performs application-defined tasks associated with freeing, 
        ///   releasing, or resetting unmanaged resources.
        /// </summary>
        /// 
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        ///   Releases unmanaged resources and performs other cleanup operations 
        ///   before the <see cref="RecorderViewModel"/> is reclaimed by garbage collection.
        /// </summary>
        /// 
        ~RecorderViewModel()
        {
            Dispose(false);
        }

        /// <summary>
        ///   Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// 
        /// <param name="disposing"><c>true</c> to release both managed
        /// and unmanaged resources; <c>false</c> to release only unmanaged
        /// resources.</param>
        ///
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // free managed resources
                if (clickCapture != null)
                {
                    clickCapture.Dispose();
                    clickCapture = null;
                }

                if (cursorCapture != null)
                {
                    cursorCapture.Dispose();
                    cursorCapture = null;
                }

                if (keyCapture != null)
                {
                    keyCapture.Dispose();
                    keyCapture = null;
                }

                if (audioDevice != null)
                {
                    audioDevice.Dispose();
                    audioDevice = null;
                }

                if (videoWriter != null)
                {
                    videoWriter.Dispose();
                    videoWriter = null;
                }
            }
        }
        #endregion


        // The PropertyChanged event doesn't needs to be explicitly raised
        // from this application. The event raising is handled automatically
        // by the NotifyPropertyWeaver VS extension using IL injection.
        //
#pragma warning disable 0067
        /// <summary>
        /// Occurs when a property value changes.
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;
#pragma warning restore 0067

    }
}
