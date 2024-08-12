using System;
using System.Linq;
using Basler.Pylon;
using System.Drawing;
using System.Drawing.Imaging;
using System.Collections.Generic;
using System.Threading;
using HalconDotNet;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.IO;

namespace Wafer_Inline_System
{
    public class Camera_Basler : ClsCameraBase
    {
        public override event Action<HObject> OnHalImageGrabbedEvent;
        public override event Action<List<HObject>> OnHalImageCountFinishedEvent;
        public override event Action<int> OnImageCountChangedEvent;

        private Camera camera;
        private CConfigCamera Config = null;
        private bool IsGrabStart = false;

        public List<HObject> ImageCountList;
        public int ImageCount = -1;
        private string CameraConfigPath = "";


        public Camera_Basler(string ID, string configDirPath) : base(ID)
        {
            ConfigPath = configDirPath + $"{ID}.xml";
            Config = new CConfigCamera();
            Config = Config.ReadXmlConfig(ConfigPath);

            ImageCountList = new List<HObject>();
        }

        public override void Init()
        {
            try
            {
                CLogger.Debug($"[{idString}] Init Start");

                ImageCount = Config.ImageCount;
                CameraConfigPath = Config.CameraConfigPath;

                IsConnect = false;

                if (Open())
                {
                    CLogger.Debug($"[{idString}] Camera Open");
                    CLogger.Debug($"[{idString}] Camera Parameter Load, Path:{CameraConfigPath}");
                    IsInit = true;
                }
                CLogger.Debug($"[{idString}] Init Finished");

            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("The device is controlled by another application. Err: An attempt was made to access an address location which is currently/momentary not accessible. (0xE1018006)"))
                {
                    OpenRetry(3);
                }
                else
                {
                    throw;
                }
            }

            ImageCountListReset();
        }
        public override bool Open()
        {
            ICameraInfo SelectedCamera = GetCameraInfo(Config.DeviceName);
            try
            {
                if (SelectedCamera == null)
                {
                    SelectedCamera = GetCameraInfo(Config.DeviceName);
                    string AllDevice = "";
                    foreach (var i in AllCameraDeviceList)
                    {
                        AllDevice += i.ToString() + Environment.NewLine;
                    }
                    throw new Exception($"[相機_{idString}]沒有匹配到相機!{Environment.NewLine}" +
                        $"1.請檢查相機設定中名稱{Config.DeviceName}是否正確。{Environment.NewLine}" +
                        $"2.請檢查相機連接網路線是否插上。{Environment.NewLine}" +
                        $"3.請檢查網路介面卡是否啟用。{Environment.NewLine}" +
                        $"***已連接相機裝置清單***{Environment.NewLine}" +
                        $"{AllDevice}");
                }

                camera = new Camera(SelectedCamera);

                if (camera.IsOpen)
                {
                    camera.Close();
                }

                camera.CameraOpened -= Configuration.AcquireContinuous;
                camera.CameraOpened += Configuration.AcquireContinuous;

                camera.ConnectionLost -= Camera_ConnectionLost;
                camera.ConnectionLost += Camera_ConnectionLost;

                camera.StreamGrabber.GrabStarted -= StreamGrabber_GrabStarted;
                camera.StreamGrabber.GrabStarted += StreamGrabber_GrabStarted;

                camera.StreamGrabber.ImageGrabbed -= StreamGrabber_ImageGrabbed;
                camera.StreamGrabber.ImageGrabbed += StreamGrabber_ImageGrabbed;

                camera.StreamGrabber.GrabStopped -= StreamGrabber_GrabStopped;
                camera.StreamGrabber.GrabStopped += StreamGrabber_GrabStopped;
                camera.Open();

                camera.Parameters.Load(CameraConfigPath, ParameterPath.CameraDevice);
                camera.Parameters[PLCamera.GevHeartbeatTimeout].SetValue(1000);
                IsConnect = true;
                return true;
            }
            catch (Exception ex)
            {
                throw;
            }
        }
        public override void Close()
        {
            if (camera != null)
            {
                camera.Close();
                camera.Dispose();
                camera = null;
                IsConnect = false;
            }
        }
        public override void LoadConfig()
        {
            Config = Config.ReadXmlConfig(ConfigPath);
        }

        public override void OneShot()
        {
            if (camera != null)
            {
                camera.Parameters[PLCamera.AcquisitionMode].SetValue(PLCamera.AcquisitionMode.SingleFrame);
                camera.StreamGrabber.Start(1, GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);

            }
        }
        int AlarmCount = 0;
        public override bool StartGrab()
        {
            if (Config.LoadImageModeEable)
            {
                if (ImageCountList.Count > 0)
                {
                    ImageCountList.ForEach(x => x.Dispose());
                    ImageCountList.Clear();
                    ImageCountList = new List<HObject>();
                }

                cts = new CancellationTokenSource();

                string VirtualImgDirPath = CProgramEnv.Instance.ProgramSystemRootDir + $"Virtual//Source_{idString}//";
                if (Directory.Exists(VirtualImgDirPath))
                {
                    string[] Images = Directory.GetFiles(VirtualImgDirPath);

                    for (int i = 0; i < Images.Length; i++)
                    {
                        if (cts.IsCancellationRequested) break;

                        HObject tempImg = new HObject();
                        try
                        {
                            HOperatorSet.ReadImage(out tempImg, Images[i]);
                            OnHalImageGrabbedEvent?.Invoke(new HObject(tempImg));
                            ImageCountList.Add(new HObject(tempImg));
                            OnImageCountChangedEvent?.Invoke(ImageCountList.Count);
                            if (ImageCountList.Count == Config.ImageCount)
                            {
                                OnHalImageCountFinishedEvent?.Invoke(ImageCountList);
                            }
                        }
                        catch (Exception ex)
                        {
                        }
                        finally
                        {
                            tempImg.Dispose();
                        }
                        Thread.Sleep(15);
                    }
                }
            }
            else
            {
                if (camera != null)
                {
                    ImageCountListReset();
                    try
                    {
                        camera.StreamGrabber.Start(GrabStrategy.OneByOne, GrabLoop.ProvidedByStreamGrabber);
                        AlarmCount = 0;
                    }
                    catch (Exception ex)
                    {
                        CLogger.Error($"[Camera_{idString}] grab err :{ex.ToString()}");
                        AlarmCount++;
                        OpenRetry(3);
                        if (AlarmCount > 3)
                        {
                            throw;
                        }
                    }
                    return true;
                }

            }
            return false;
        }
        public override void StopGrab()
        {
            if (camera != null)
            {
                camera.StreamGrabber.Stop();
                cts.Cancel();

            }
        }
        public void ImageCountListReset()
        {
            ImageCountList.ForEach(x => x.Dispose());
            GC.Collect();
            Thread.Yield();
            ImageCountList.Clear();
            ImageCountList = new List<HObject>();
            OnImageCountChangedEvent?.Invoke(ImageCountList.Count);

        }
        List<string> AllCameraDeviceList;
        private ICameraInfo GetCameraInfo(String findId)
        {
            try
            {
                List<ICameraInfo> allCameras = new List<ICameraInfo>();
                allCameras.Clear();
                Task.Factory.StartNew(() =>
                {
                    while (true)
                    {
                        allCameras = CameraFinder.Enumerate();
                        if (allCameras.Count != 0)
                        {
                            break;
                        }
                        Thread.Sleep(1000);
                    }
                }).Wait(6000);

                allCameras = CameraFinder.Enumerate();
                AllCameraDeviceList?.Clear();
                AllCameraDeviceList = new List<string>();

                int count = 1;
                foreach (ICameraInfo cameraInfo in allCameras)
                {
                    string fullName = cameraInfo[CameraInfoKey.FullName];
                    string DeviceUserDefinedName = cameraInfo[CameraInfoKey.UserDefinedName];
                    if (fullName != null)
                    {
                        string[] A = fullName.Split('#');
                        string type = A[0];
                        fullName = fullName.Replace(type, "");
                    }
                    AllCameraDeviceList.Add($"[{count}]{DeviceUserDefinedName}{fullName}");
                    count++;
                    if (DeviceUserDefinedName == findId)
                    {
                        return cameraInfo;
                    }
                }

                return null;

            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message);
                //throw new Exception("開發時期必須排除??");
            }

        }

        public bool OpenRetry(uint retryCount)
        {
            ClsNetWorkInterface csNetWorkInterface = new ClsNetWorkInterface();
            Task.Factory.StartNew(() =>
            {
                csNetWorkInterface.RestartNetwork(Config.DeviceName + "0");

            }).ContinueWith(t =>
            {
                try
                {
                    if (Open() == false)
                    {
                        throw new Exception($"[Camera_{idString}] The Camera is controlled by another application. Please restart the camera.");
                    }
                }
                catch (Exception ex)
                {
                    CLogger.Error($"[Camera_{idString}] Open Retry err, {ex}");

                    throw new Exception($"[Camera_{idString}] The Camera is controlled by another application. Please restart the camera.");
                }
            });


            return IsConnect;
        }
        private void StreamGrabber_GrabStarted(object sender, EventArgs e)
        {
            IsGrabStart = true;
        }
        private void StreamGrabber_ImageGrabbed(object sender, ImageGrabbedEventArgs e)
        {
            IGrabResult grabResult = e.GrabResult;
            if (grabResult.IsValid)
            {
                if (IsGrabStart)
                {

                    HObject HalImgTemp = ClsMisc.IGrabResultToHObject(grabResult);
                    OnHalImageGrabbedEvent?.Invoke(new HObject(HalImgTemp));
                    ImageCountList.Add(new HObject(HalImgTemp));
                    OnImageCountChangedEvent?.Invoke(ImageCountList.Count);

                    if (ImageCount != -1)
                    {
                        if (ImageCountList.Count >= ImageCount)
                        {
                            StopGrab();
                            OnHalImageCountFinishedEvent?.Invoke(ImageCountList);
                        }
                    }
                    //HalImgTemp.Dispose();
                }
            }
        }
        private void StreamGrabber_GrabStopped(object sender, GrabStopEventArgs e)
        {
            IsGrabStart = false;
        }

        private void Camera_ConnectionLost(object sender, EventArgs e)
        {
            CLogger.Error($"[Camera_{idString}] Connect Lost");

            camera.StreamGrabber.Stop();
            IsConnect = false;
            OpenRetry(3);
        }
    }
}
