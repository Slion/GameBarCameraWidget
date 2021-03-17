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

namespace GameBarCameraWidget
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
        private SoftwareBitmapSource iSoftwareBitmapSource;

        Point iPointerDown = new Point(0, 0);
        Point iPointerUp = new Point(1920, 1080);
        Rect iRect = new Rect(0, 0, 1920, 1080);
        bool iIsPointerDown = false;
        bool iCameraStopped = false;
        bool iNewCroppingRect = true;

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
            Application.Current.EnteredBackground += AppEnteredBackground;
            Application.Current.LeavingBackground += AppLeavingBackground;


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
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        iErrorMessage.Text = ex.Message;
                    });
                }

                // Set our processed resulting image
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        await iSoftwareBitmapSource.SetBitmapAsync(targetSoftwareBitmap);
                        // Needed in case cropping changed
                        if (iNewCroppingRect)
                        {
                            ApplyAlignment();
                            iNewCroppingRect = false;
                        }                        
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                        {
                            iErrorMessage.Text = ex.Message;
                        });
                    }
                }

                );
      

                //.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                //CurrentFrameImage.Source = softwareBitmap;
            }
        }

        // Register for PreviewFailed to get failure error information.
        private async void CameraPreviewControl_PreviewFailed(object sender, PreviewFailedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                iErrorMessage.Text = e.Error;
            });            
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

                deferral.Complete();

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    iErrorMessage.Text = "Application suspending";
                });

            }


        }

        private async void Application_Resuming(object sender, object o)
        {
            // Handle global application events only if this page is active
            if (Frame.CurrentSourcePageType == typeof(WidgetCamera))
            {
                // Populate orientation variables with the current state and register for future changes


                //await InitializeCameraAsync();

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    iErrorMessage.Text = "Application resuming";
                });


            }
        }



        private async void AppLeavingBackground(object sender, LeavingBackgroundEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                iErrorMessage.Text = "Application leaving background";
            });
        }

        private async void AppEnteredBackground(object sender, EnteredBackgroundEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                iErrorMessage.Text = "Application entered background";
            });
        }



        double SettingOpacity 
        { 
            get 
            {
                var val = App.Settings["opacity"];
                if (val!=null)
                {
                    return (double)val;
                }
                else
                {
                    return 50;
                }                
            }
            set { App.Settings["opacity"] = value; }
        }

        bool SettingOpacityOverride
        {
            get
            {
                var val = App.Settings["opacity-override"];
                if (val!=null)
                {
                    return (bool)val; 
                }
                else
                {
                    return false;
                }
            }
            set { App.Settings["opacity-override"] = value; }
        }

        Rect SettingCroppingRect
        {
            get
            {
                var rect = App.Settings["cropping-rect"];
                if (rect == null)
                {
                    return new Rect(0, 0, frameWidth, frameHeight);
                }
                else
                {
                    return (Rect)rect;
                }
            }
            set { App.Settings["cropping-rect"] = value; }
        }


        string SettingHorizontalAlignmentString
        {
            get
            {
                var val = App.Settings["halign"];
                if (val != null)
                {
                    return (string)val;
                }
                else
                {
                    return "Center";
                }
            }
            set 
            { 
                App.Settings["halign"] = value;
                iImageCamera.HorizontalAlignment = Enum.Parse<HorizontalAlignment>(value);
                iImageCameraPreview.HorizontalAlignment = Enum.Parse<HorizontalAlignment>(value);
            }
        }

        HorizontalAlignment SettingHorizontalAlignment
        {
            get
            {
                return Enum.Parse<HorizontalAlignment>(SettingHorizontalAlignmentString);
            }
            set 
            {               
                SettingHorizontalAlignmentString = value.ToString();
            }
        }


        string SettingVerticalAlignmentString
        {
            get
            {
                var val = App.Settings["valign"];
                if (val != null)
                {
                    return (string)val;
                }
                else
                {
                    return "Center";
                }
            }
            set
            {
                App.Settings["valign"] = value;
                iImageCamera.VerticalAlignment = Enum.Parse<VerticalAlignment>(value);
                iImageCameraPreview.VerticalAlignment = Enum.Parse<VerticalAlignment>(value);
            }
        }

        VerticalAlignment SettingVerticalAlignment
        {
            get
            {
                return Enum.Parse<VerticalAlignment>(SettingVerticalAlignmentString);
            }
            set
            {
                SettingVerticalAlignmentString = value.ToString();
            }
        }



        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            iWidget = e.Parameter as XboxGameBarWidget;

            // Hook up the settings clicked event
            iWidget.SettingsClicked += Widget_SettingsClicked;
            iWidget.GameBarDisplayModeChanged += Widget_GameBarDisplayModeChanged;
            iWidget.VisibleChanged += Widget_VisibleChanged;
            iWidget.RequestedThemeChanged += Widget_RequestedThemeChanged;

            // Create a software bitmap source and set it to the Xaml Image control source.
            iSoftwareBitmapSource = new SoftwareBitmapSource();
            iImageCameraPreview.Source = iSoftwareBitmapSource;
            iImageCamera.Source = iSoftwareBitmapSource;
            // Not working for some reason, could be because it not available when only one camera on your system
            //iCameraPreview.IsFrameSourceGroupButtonVisible = true;

        }

        private async void Widget_RequestedThemeChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                RequestedTheme = iWidget.RequestedTheme;
            });
        }

        private async void Widget_VisibleChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                if (iWidget.Visible)
                {
                    if (iCameraPreview.CameraHelper==null || iCameraStopped == true)
                    {
                        // Initialize the CameraPreview control and subscribe to the events
                        iCameraPreview.PreviewFailed += CameraPreviewControl_PreviewFailed;
                        iErrorMessage.Text = "Starting camera...";
                        await iCameraPreview.StartAsync();
                        iErrorMessage.Text = "Camera started.";
                        iCameraPreview.CameraHelper.FrameArrived += CameraPreviewControl_FrameArrived;
                        iRect = SettingCroppingRect;
                        iCameraStopped = false;
                    }
                    else 
                    {
                        iErrorMessage.Text = "Camera already started.";
                    }
                }
                else
                {
                    // Sadly this is not called when locking the screen with Win+L and our widget is pinned
                    iCameraPreview.PreviewFailed -= CameraPreviewControl_PreviewFailed;
                    iCameraPreview.CameraHelper.FrameArrived -= CameraPreviewControl_FrameArrived;                    
                    iCameraPreview.Stop();
                    iErrorMessage.Text = "Stopping camera...";
                    await iCameraPreview.CameraHelper.CleanUpAsync();
                    iErrorMessage.Text = "Camera stopped.";
                    iCameraStopped = true;
                }

            });            
        }


        private void SwitchToCameraMode()
        {
            iGridSettings.Visibility = Visibility.Collapsed;
            iGridCamera.Visibility = Visibility.Visible;
        }

        private void SwitchToSettingsMode()
        {
            iGridSettings.Visibility = Visibility.Visible;
            iGridCamera.Visibility = Visibility.Collapsed;
        }

        void ApplyAlignment()
        {            
            iImageCamera.HorizontalAlignment = SettingHorizontalAlignment;
            iImageCameraPreview.HorizontalAlignment = SettingHorizontalAlignment;
            iImageCamera.VerticalAlignment = SettingVerticalAlignment;
            iImageCameraPreview.VerticalAlignment = SettingVerticalAlignment;
            iImageCamera.InvalidateArrange();
            iImageCameraPreview.InvalidateArrange();
        }


        private async void Widget_GameBarDisplayModeChanged(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (iWidget.GameBarDisplayMode == XboxGameBarDisplayMode.PinnedOnly)
                {
                    // Pinned mode switch to camera mode
                    SwitchToCameraMode();
                    if (!SettingOpacityOverride)
                    {
                        // Apply requested system opacity then                    
                        iImageCamera.Opacity = iWidget.RequestedOpacity;
                    }
                    else 
                    {
                        // Apply custom widget defined opacity then
                        iImageCamera.Opacity = SettingOpacity/100;
                    }
                }
                else if (iWidget.GameBarDisplayMode == XboxGameBarDisplayMode.Foreground)
                {
                    SwitchToSettingsMode();
                    // No opacity in Game Bar Foreground mode
                    iImageCamera.Opacity = 1.0;
                }

                ApplyAlignment();
            });
        }

        private async void Widget_SettingsClicked(XboxGameBarWidget sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
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
        }

        #endregion Constructor, lifecycle and navigation


        private void CameraPreviewControl_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            iPointerDown =  e.GetCurrentPoint((UIElement)sender).Position;
            e.Handled = true;
            iIsPointerDown = true;

            //ErrorMessage.Text = iStartPoint.ToString();
        }


        private void CameraPreviewControl_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            iPointerUp = e.GetCurrentPoint((UIElement)sender).Position;
            e.Handled = true;
            iIsPointerDown = false;

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
            Point startPoint = new Point((iPointerDown.X - widthDiffHalf) * widthRatio, iPointerDown.Y * heightRatio);
            Point endPoint = new Point((iPointerUp.X - widthDiffHalf) * widthRatio, iPointerUp.Y * heightRatio);                       

            // Compute frame space cropping rectangle
            iRect = new Rect(startPoint, endPoint);
            SettingCroppingRect = iRect;
            iNewCroppingRect = true;

            //ErrorMessage.Text = iEndPoint.ToString() + " - " + iRect.ToString();
        }

        private void CameraPreviewControl_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!iIsPointerDown) return;
            
            e.Handled = true;

            // Draw rectangle in control space
            Point currentPoint = e.GetCurrentPoint((UIElement)sender).Position;
            Rect rect = new Rect(iPointerDown, currentPoint);
            //
            iRectangle.Width = rect.Width;
            iRectangle.Height = rect.Height;
            Canvas.SetLeft(iRectangle, rect.Left);
            Canvas.SetTop(iRectangle, rect.Top);

            iRectangle.Visibility = Visibility.Visible;

            //ErrorMessage.Text = iStartPoint.ToString() + " - " + currentPoint.ToString();
        }

        private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // As we don't bother scalling it
            iRectangle.Visibility = Visibility.Collapsed;
        }
    }
}