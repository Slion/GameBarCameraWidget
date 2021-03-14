using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Restricted;
using Windows.UI.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Devices.Enumeration;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.System.Display;
using Windows.UI.Xaml.Media.Imaging;
using Microsoft.Toolkit.Uwp.Helpers;
using Microsoft.Toolkit.Uwp.UI.Controls;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Shapes;

namespace XboxGameBarCamera
{
    [ComImport]
    [Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }

    public sealed partial class WidgetCamera : Page
    {
        private XboxGameBarWidget iWidget = null;

        // Receive notifications about rotation of the UI and apply any necessary rotation to the preview stream
        private readonly DisplayInformation _displayInformation = DisplayInformation.GetForCurrentView();
        private DisplayOrientations _displayOrientation = DisplayOrientations.Portrait;

        // Rotation metadata to apply to the preview stream (MF_MT_VIDEO_ROTATION)
        // Reference: http://msdn.microsoft.com/en-us/library/windows/apps/xaml/hh868174.aspx
        private static readonly Guid RotationKey = new Guid("C380465D-2271-428C-9B83-ECEA3B4A85C1");

        // Folder in which the captures will be stored (initialized in InitializeCameraAsync)
        private StorageFolder _captureFolder = null;

        // Prevent the screen from sleeping while the camera is running
        private readonly DisplayRequest _displayRequest = new DisplayRequest();

        // For listening to media property changes
        private readonly SystemMediaTransportControls _systemMediaControls = SystemMediaTransportControls.GetForCurrentView();

        // MediaCapture and its state variables
        private MediaCapture _mediaCapture;
        private bool _isInitialized = false;
        private bool _isPreviewing = false;
        private static readonly SemaphoreSlim _mediaCaptureLifeLock = new SemaphoreSlim(1);

        // Information about the camera device
        private bool _mirroringPreview = false;
        private bool _externalCamera = false;

        private SoftwareBitmapSource _softwareBitmapSource;



        private uint frameHeight { get { return iCameraPreview.CameraHelper.PreviewFrameSource.CurrentFormat.VideoFormat.Height; } }
        private uint frameWidth { get { return iCameraPreview.CameraHelper.PreviewFrameSource.CurrentFormat.VideoFormat.Width; } }


        #region Constructor, lifecycle and navigation

        public WidgetCamera()
        {
            this.InitializeComponent();

            // Cache the UI to have the checkboxes retain their state, as the enabled/disabled state of the
            // GetPreviewFrameButton is reset in code when suspending/navigating (see Start/StopPreviewAsync)
            NavigationCacheMode = NavigationCacheMode.Required;

            // Useful to know when to initialize/clean up the camera
            Application.Current.Suspending += Application_Suspending;
            Application.Current.Resuming += Application_Resuming;



        }



        // Register for FrameArrived to get real time video frames, software bitmaps. 
        private async void CameraPreviewControl_FrameArrived(object sender, FrameEventArgs e)
        {
            var videoFrame = e.VideoFrame;
            var softwareBitmap = e.VideoFrame.SoftwareBitmap;
            var targetSoftwareBitmap = softwareBitmap;

            if (softwareBitmap != null)
            {
                // Convert bitmap so that it can be displayed in our Image control, I believe
                if (softwareBitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8 || softwareBitmap.BitmapAlphaMode == BitmapAlphaMode.Straight)
                {
                    targetSoftwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                }

                // Crop
                try
                {
                    targetSoftwareBitmap = await CreateFromBitmap(targetSoftwareBitmap, iRect);
                }
                catch (Exception ex)
                {
                    await ErrorMessage.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        ErrorMessage.Text = ex.Message;
                    });
                }

                // Set our processed resulting image
                await _softwareBitmapSource.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        await _softwareBitmapSource.SetBitmapAsync(targetSoftwareBitmap);
                    }
                    catch (Exception ex)
                    {
                        await ErrorMessage.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            ErrorMessage.Text = ex.Message;
                        });
                    }
                }

                );
      

                //.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                //CurrentFrameImage.Source = softwareBitmap;
            }
        }

        // Register for PreviewFailed to get failure error information.
        private void CameraPreviewControl_PreviewFailed(object sender, PreviewFailedEventArgs e)
        {
            ErrorMessage.Text = e.Error;
        }

        /// <summary>
        /// Does image cropping.
        /// </summary>
        /// <param name="softwareBitmap"></param>
        /// <param name="rect"></param>
        /// <returns></returns>
        private async Task<SoftwareBitmap> CreateFromBitmap(SoftwareBitmap softwareBitmap, Rect rect)
        {
            using (InMemoryRandomAccessStream stream = new InMemoryRandomAccessStream())
            {
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);

                encoder.SetSoftwareBitmap(softwareBitmap);

                encoder.BitmapTransform.Bounds = new BitmapBounds()
                {
                    X = (uint)rect.X,
                    Y = (uint)rect.Y,
                    Height = (uint)rect.Height,
                    Width = (uint)rect.Width
                };

                await encoder.FlushAsync();

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
                return await decoder.GetSoftwareBitmapAsync(softwareBitmap.BitmapPixelFormat, softwareBitmap.BitmapAlphaMode);
            }
        }


        private async void Application_Suspending(object sender, SuspendingEventArgs e)
        {
            // Handle global application events only if this page is active
            if (Frame.CurrentSourcePageType == typeof(WidgetCamera))
            {
                var deferral = e.SuspendingOperation.GetDeferral();

                await CleanupCameraAsync();

                _displayInformation.OrientationChanged -= DisplayInformation_OrientationChanged;

                deferral.Complete();
            }
        }

        private async void Application_Resuming(object sender, object o)
        {
            // Handle global application events only if this page is active
            if (Frame.CurrentSourcePageType == typeof(WidgetCamera))
            {
                // Populate orientation variables with the current state and register for future changes
                _displayOrientation = _displayInformation.CurrentOrientation;
                _displayInformation.OrientationChanged += DisplayInformation_OrientationChanged;

                //await InitializeCameraAsync();
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            iWidget = e.Parameter as XboxGameBarWidget;

            // Hook up the settings clicked event
            iWidget.SettingsClicked += Widget_SettingsClicked;
            iWidget.GameBarDisplayModeChanged += Widget_GameBarDisplayModeChanged;

            // Populate orientation variables with the current state and register for future changes
            _displayOrientation = _displayInformation.CurrentOrientation;
            _displayInformation.OrientationChanged += DisplayInformation_OrientationChanged;

            //await InitializeCameraAsync();

            // Initialize the CameraPreview control and subscribe to the events
            iCameraPreview.PreviewFailed += CameraPreviewControl_PreviewFailed;
            await iCameraPreview.StartAsync();
            iCameraPreview.CameraHelper.FrameArrived += CameraPreviewControl_FrameArrived;

            // Create a software bitmap source and set it to the Xaml Image control source.
            _softwareBitmapSource = new SoftwareBitmapSource();
            CurrentFrameImage.Source = _softwareBitmapSource;
            iImageCamera.Source = _softwareBitmapSource;
            // Not working for some reason
            //iCameraPreview.IsFrameSourceGroupButtonVisible = true;


            //iMediaPlayElement = (MediaPlayerElement)iCameraPreview.GetTemplateChild("MediaPlayerElementControl");


        }

        private async void Widget_GameBarDisplayModeChanged(XboxGameBarWidget sender, object args)
        {
            await iGridSettings.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (iWidget.GameBarDisplayMode == XboxGameBarDisplayMode.PinnedOnly)
                {
                    // Pinned mode switch to camera mode
                    iGridSettings.Visibility = Visibility.Collapsed;
                    iGridCamera.Visibility = Visibility.Visible;
                }
            });


        }

        private async void Widget_SettingsClicked(XboxGameBarWidget sender, object args)
        {
            await iGridSettings.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                // Toggle bitween settings mode and camera mode
                if (iGridSettings.Visibility == Visibility.Visible)
                {
                    iGridSettings.Visibility = Visibility.Collapsed;
                    iGridCamera.Visibility = Visibility.Visible;
                }
                else
                {
                    iGridSettings.Visibility = Visibility.Visible;
                    iGridCamera.Visibility = Visibility.Collapsed;
                }
            });
        }

        protected override async void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            // Handling of this event is included for completenes, as it will only fire when navigating between pages and this sample only includes one page

            await CleanupCameraAsync();

            _displayInformation.OrientationChanged -= DisplayInformation_OrientationChanged;
        }

        #endregion Constructor, lifecycle and navigation


        #region Event handlers

        /// <summary>
        /// In the event of the app being minimized this method handles media property change events. If the app receives a mute
        /// notification, it is no longer in the foregroud.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private async void SystemMediaControls_PropertyChanged(SystemMediaTransportControls sender, SystemMediaTransportControlsPropertyChangedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                // Only handle this event if this page is currently being displayed
                if (args.Property == SystemMediaTransportControlsProperty.SoundLevel && Frame.CurrentSourcePageType == typeof(MainPage))
                {
                    // Check to see if the app is being muted. If so, it is being minimized.
                    // Otherwise if it is not initialized, it is being brought into focus.
                    if (sender.SoundLevel == SoundLevel.Muted)
                    {
                        await CleanupCameraAsync();
                    }
                    else if (!_isInitialized)
                    {
                        //await InitializeCameraAsync();
                    }
                }
            });
        }

        /// <summary>
        /// This event will fire when the page is rotated
        /// </summary>
        /// <param name="sender">The event source.</param>
        /// <param name="args">The event data.</param>
        private async void DisplayInformation_OrientationChanged(DisplayInformation sender, object args)
        {
            _displayOrientation = sender.CurrentOrientation;

            if (_isPreviewing)
            {
                await SetPreviewRotationAsync();
            }
        }



        #endregion Event handlers


        #region MediaCapture methods





        /// <summary>
        /// Gets the current orientation of the UI in relation to the device and applies a corrective rotation to the preview
        /// </summary>
        private async Task SetPreviewRotationAsync()
        {
            // Only need to update the orientation if the camera is mounted on the device
            if (_externalCamera) return;

            // Calculate which way and how far to rotate the preview
            int rotationDegrees = ConvertDisplayOrientationToDegrees(_displayOrientation);

            // The rotation direction needs to be inverted if the preview is being mirrored
            if (_mirroringPreview)
            {
                rotationDegrees = (360 - rotationDegrees) % 360;
            }

            // Add rotation metadata to the preview stream to make sure the aspect ratio / dimensions match when rendering and getting preview frames
            var props = _mediaCapture.VideoDeviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview);
            props.Properties.Add(RotationKey, rotationDegrees);
            await _mediaCapture.SetEncodingPropertiesAsync(MediaStreamType.VideoPreview, props, null);
        }

        /// <summary>
        /// Stops the preview and deactivates a display request, to allow the screen to go into power saving modes, and locks the UI
        /// </summary>
        /// <returns></returns>
        private async Task StopPreviewAsync()
        {
            _isPreviewing = false;
            await _mediaCapture.StopPreviewAsync();

            // Use the dispatcher because this method is sometimes called from non-UI threads
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                //PreviewControl.Source = null;

                // Allow the device to sleep now that the preview is stopped
                _displayRequest.RequestRelease();

                //GetPreviewFrameButton.IsEnabled = _isPreviewing;
            });
        }




        /// <summary>
        /// Cleans up the camera resources (after stopping the preview if necessary) and unregisters from MediaCapture events
        /// </summary>
        /// <returns></returns>
        private async Task CleanupCameraAsync()
        {
            await _mediaCaptureLifeLock.WaitAsync();

            try
            {
                if (_isInitialized)
                {
                    if (_isPreviewing)
                    {
                        // The call to stop the preview is included here for completeness, but can be
                        // safely removed if a call to MediaCapture.Dispose() is being made later,
                        // as the preview will be automatically stopped at that point
                        await StopPreviewAsync();
                    }

                    _isInitialized = false;
                }

                if (_mediaCapture != null)
                {
                    //_mediaCapture.Failed -= MediaCapture_Failed;
                    _mediaCapture.Dispose();
                    _mediaCapture = null;
                }
            }
            finally
            {
                _mediaCaptureLifeLock.Release();
            }
        }

        #endregion MediaCapture methods


        #region Helper functions

        /// <summary>
        /// Queries the available video capture devices to try and find one mounted on the desired panel
        /// </summary>
        /// <param name="desiredPanel">The panel on the device that the desired camera is mounted on</param>
        /// <returns>A DeviceInformation instance with a reference to the camera mounted on the desired panel if available,
        ///          any other camera if not, or null if no camera is available.</returns>
        private static async Task<DeviceInformation> FindCameraDeviceByPanelAsync(Windows.Devices.Enumeration.Panel desiredPanel)
        {
            // Get available devices for capturing pictures
            var allVideoDevices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);

            // Get the desired camera by panel
            DeviceInformation desiredDevice = allVideoDevices.FirstOrDefault(x => x.EnclosureLocation != null && x.EnclosureLocation.Panel == desiredPanel);

            // If there is no device mounted on the desired panel, return the first device found
            return desiredDevice ?? allVideoDevices.FirstOrDefault();
        }

        /// <summary>
        /// Converts the given orientation of the app on the screen to the corresponding rotation in degrees
        /// </summary>
        /// <param name="orientation">The orientation of the app on the screen</param>
        /// <returns>An orientation in degrees</returns>
        private static int ConvertDisplayOrientationToDegrees(DisplayOrientations orientation)
        {
            switch (orientation)
            {
                case DisplayOrientations.Portrait:
                    return 90;
                case DisplayOrientations.LandscapeFlipped:
                    return 180;
                case DisplayOrientations.PortraitFlipped:
                    return 270;
                case DisplayOrientations.Landscape:
                default:
                    return 0;
            }
        }

        /// <summary>
        /// Saves a SoftwareBitmap to the specified StorageFile
        /// </summary>
        /// <param name="bitmap">SoftwareBitmap to save</param>
        /// <param name="file">Target StorageFile to save to</param>
        /// <returns></returns>
        private static async Task SaveSoftwareBitmapAsync(SoftwareBitmap bitmap, StorageFile file)
        {
            using (var outputStream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);

                // Grab the data from the SoftwareBitmap
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync();
            }
        }

        /// <summary>
        /// Applies a basic effect to a Bgra8 SoftwareBitmap in-place
        /// </summary>
        /// <param name="bitmap">SoftwareBitmap that will receive the effect</param>
        private unsafe void ApplyGreenFilter(SoftwareBitmap bitmap)
        {
            // Effect is hard-coded to operate on BGRA8 format only
            if (bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8)
            {
                // In BGRA8 format, each pixel is defined by 4 bytes
                const int BYTES_PER_PIXEL = 4;

                using (var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.ReadWrite))
                using (var reference = buffer.CreateReference())
                {
                    if (reference is IMemoryBufferByteAccess)
                    {
                        // Get a pointer to the pixel buffer
                        byte* data;
                        uint capacity;
                        ((IMemoryBufferByteAccess)reference).GetBuffer(out data, out capacity);

                        // Get information about the BitmapBuffer
                        var desc = buffer.GetPlaneDescription(0);

                        // Iterate over all pixels
                        for (uint row = 0; row < desc.Height; row++)
                        {
                            for (uint col = 0; col < desc.Width; col++)
                            {
                                // Index of the current pixel in the buffer (defined by the next 4 bytes, BGRA8)
                                var currPixel = desc.StartIndex + desc.Stride * row + BYTES_PER_PIXEL * col;

                                // Read the current pixel information into b,g,r channels (leave out alpha channel)
                                var b = data[currPixel + 0]; // Blue
                                var g = data[currPixel + 1]; // Green
                                var r = data[currPixel + 2]; // Red

                                // Boost the green channel, leave the other two untouched
                                data[currPixel + 0] = b;
                                data[currPixel + 1] = (byte)Math.Min(g + 80, 255);
                                data[currPixel + 2] = r;
                            }
                        }
                    }
                }
            }
        }

        #endregion Helper functions 


        Point iStartPoint = new Point(0,0);
        Point iEndPoint = new Point(1920, 1080);

        Rect iRect = new Rect(0,0,1920,1080);
        bool iPointerDown = false;

        private void CameraPreviewControl_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            iStartPoint =  e.GetCurrentPoint((UIElement)sender).Position;
            e.Handled = true;
            iPointerDown = true;

            //ErrorMessage.Text = iStartPoint.ToString();
        }

        private void CameraPreviewControl_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            iEndPoint = e.GetCurrentPoint((UIElement)sender).Position;
            e.Handled = true;
            iPointerDown = false;

            // Compute coordinates of corresponding points in frame space
            // We need to take into account that the camera frame takes only a fraction of preview control, we have space right and left of the frame
            // I guess we kind of assume that the video frame is larger than it is wide here and thus is bound by the height of the camera preview control
            // Compute our height ratio assuming scalled video frame take the whole height of our camera preview control
            double heightRatio = frameHeight / iCameraPreview.ActualHeight;
            // From there we can compute how wide is the video frame in control space
            double widthDiff = iCameraPreview.ActualWidth - (frameWidth / heightRatio);
            // Compute width of blank spaces  left and right of our video frame, this is needed to offset our coordinates
            double widthDiffHalf = widthDiff / 2;
            // Compute our width ratio 
            double widthRatio = frameWidth / (iCameraPreview.ActualWidth - widthDiff);

            // Now 
            Point startPoint = new Point((iStartPoint.X - widthDiffHalf) * widthRatio, iStartPoint.Y * heightRatio);
            Point endPoint = new Point((iEndPoint.X - widthDiffHalf) * widthRatio, iEndPoint.Y * heightRatio);                       

            // Compute frame space cropping rectangle
            iRect = new Rect(startPoint, endPoint);

            //ErrorMessage.Text = iEndPoint.ToString() + " - " + iRect.ToString();
        }

        private void CameraPreviewControl_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!iPointerDown) return;
            
            e.Handled = true;

            // Draw rectangle in control space
            Point currentPoint = e.GetCurrentPoint((UIElement)sender).Position;
            Rect rect = new Rect(iStartPoint, currentPoint);

            //rectangle1.Fill = new SolidColorBrush(Windows.UI.Colors.Blue);
            iRectangle.Width = rect.Width;
            iRectangle.Height = rect.Height;
            //iRectangle.Stroke = new SolidColorBrush(Windows.UI.Colors.Black);
            //iRectangle.StrokeThickness = 3;
            //rectangle1.Pos

            Canvas.SetLeft(iRectangle, rect.Left);
            Canvas.SetTop(iRectangle, rect.Top);

            iRectangle.Visibility = Visibility.Visible;

            //ErrorMessage.Text = iStartPoint.ToString() + " - " + currentPoint.ToString();

            // When you create a XAML element in code, you have to add
            // it to the XAML visual tree. This example assumes you have
            // a panel named 'layoutRoot' in your XAML file, like this:
            // <Grid x:Name="layoutRoot>
            //iCanvas.Children.Add(rectangle);
        }

        private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // As we don't bother scalling it
            iRectangle.Visibility = Visibility.Collapsed;
        }
    }
}