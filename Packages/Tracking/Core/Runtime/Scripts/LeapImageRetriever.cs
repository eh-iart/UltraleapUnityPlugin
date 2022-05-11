/******************************************************************************
 * Copyright (C) Ultraleap, Inc. 2011-2021.                                   *
 *                                                                            *
 * Use subject to the terms of the Apache License 2.0 available at            *
 * http://www.apache.org/licenses/LICENSE-2.0, or another agreement           *
 * between Ultraleap and you, your company or other organization.             *
 ******************************************************************************/

using Leap.Unity.Query;
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Serialization;

namespace Leap.Unity
{

    /// <summary>
    /// Acquires images from a LeapServiceProvider and uploads image data as shader global
    /// data for use by any shaders that render those images.
    /// 
    /// Note: To use the LeapImageRetriever, you must be on version 2.1 or newer and you
    /// must enable "Allow Images" in your Leap Motion settings.
    /// </summary>
    public class LeapImageRetriever : MonoBehaviour
    {
        public const string GLOBAL_COLOR_SPACE_GAMMA_NAME = "_LeapGlobalColorSpaceGamma";
        public const string GLOBAL_GAMMA_CORRECTION_EXPONENT_NAME = "_LeapGlobalGammaCorrectionExponent";
        public const string GLOBAL_CAMERA_PROJECTION_NAME = "_LeapGlobalProjection";
        public const int IMAGE_WARNING_WAIT = 10;
        public const int LEFT_IMAGE_INDEX = 0;
        public const int RIGHT_IMAGE_INDEX = 1;
        public const float IMAGE_SETTING_POLL_RATE = 2.0f;
        public const int MAX_NUMBER_OF_GLOBAL_TEXTURES = 5;

        [SerializeField]
        [FormerlySerializedAs("gammaCorrection")]
        private float _gammaCorrection = 1.0f;

        [SerializeField] private LeapServiceProvider _provider;
        private EyeTextureData _eyeTextureData = new EyeTextureData();

        //Image that we have requested from the service.  Are requested in Update and retrieved in OnPreRender
        protected ProduceConsumeBuffer<Image> _imageQueue = new ProduceConsumeBuffer<Image>(128);
        protected Image _currentImage = null;

        private long _prevSequenceId;
        private bool _needQueueReset;

        //Rigel tracking cameras produce debug info in the image output, enable this to hide it.
        [field: SerializeField]
        public bool HideRigelDebug { get; set; }

        // If image IDs from the libtrack server do not reset with the Visualiser, it triggers out-of-sequence
        // checks and we lose images. Detecting this and setting an offset allows us to compensate.
        private long _frameIDOffset = -1;

        public EyeTextureData TextureData
        {
            get
            {
                return _eyeTextureData;
            }
        }

        public class LeapTextureData
        {
            private Texture2DArray _globalRawTextures = null;
            private Texture2D _combinedTexture = null;
            private byte[] _intermediateArray = null;

            private bool _hideLeapDebugInfo = true;
            public void HideDebugInfo(bool hideDebug)
            {
                _hideLeapDebugInfo = hideDebug;
            }

            public Texture2D CombinedTexture
            {
                get
                {
                    return _combinedTexture;
                }
            }

            public bool CheckStale(Image image)
            {
                if (_combinedTexture == null || _intermediateArray == null)
                {
                    return true;
                }

                if (image.Width != _combinedTexture.width || image.Height * 2 != _combinedTexture.height)
                {
                    return true;
                }

                if (_combinedTexture.format != getTextureFormat(image))
                {
                    return true;
                }

                return false;
            }

            public void Reconstruct(Image image, string globalShaderName, string pixelSizeName, int deviceID)
            {
                if (deviceID >= MAX_NUMBER_OF_GLOBAL_TEXTURES)
                {
                    Debug.LogWarning("DeviceID too high: " + deviceID);
                    return;
                }

                int combinedWidth = image.Width;
                int combinedHeight = image.Height * 2;

                TextureFormat format = getTextureFormat(image);

                if (_combinedTexture != null)
                {
                    DestroyImmediate(_combinedTexture);
                }

                _combinedTexture = new Texture2D(combinedWidth, combinedHeight, format, false, true);
                _combinedTexture.wrapMode = TextureWrapMode.Clamp;
                _combinedTexture.filterMode = FilterMode.Bilinear;
                _combinedTexture.name = globalShaderName;
                _combinedTexture.hideFlags = HideFlags.DontSave;

                _intermediateArray = new byte[combinedWidth * combinedHeight * bytesPerPixel(format)];

                Texture temp = Shader.GetGlobalTexture(globalShaderName);
                if (temp == null || temp.dimension != UnityEngine.Rendering.TextureDimension.Tex2DArray)
                {
                    // rawTextureWidth and Height are a bigger than the combinedTexture width and height, 
                    // so that all different textures (from different devices) fit into it
                    int rawTextureWidth = 800;
                    int rawTextureHeight = 800;
                    _globalRawTextures = new Texture2DArray(rawTextureWidth, rawTextureHeight, MAX_NUMBER_OF_GLOBAL_TEXTURES, _combinedTexture.format, false, true);
                    _globalRawTextures.wrapMode = TextureWrapMode.Clamp;
                    _globalRawTextures.filterMode = FilterMode.Bilinear;
                    _globalRawTextures.hideFlags = HideFlags.DontSave;
                    Shader.SetGlobalTexture(globalShaderName, _globalRawTextures);
                }
                else
                {
                    _globalRawTextures = (Texture2DArray)temp;
                }

                // set factors to multiply to uv coordinates, so that we sample from the globalRawTexture where it is actually filled with the _combinedTexture
                Vector4[] textureSizeFactors = Shader.GetGlobalVectorArray("_LeapGlobalTextureSizeFactor");
                if (textureSizeFactors == null)
                {
                    textureSizeFactors = new Vector4[MAX_NUMBER_OF_GLOBAL_TEXTURES];
                }
                textureSizeFactors[deviceID] = new Vector4((float)_combinedTexture.width / _globalRawTextures.width, (float)_combinedTexture.height / _globalRawTextures.height, 0, 0);
                Shader.SetGlobalVectorArray("_LeapGlobalTextureSizeFactor", textureSizeFactors);

                Shader.SetGlobalVector(pixelSizeName, new Vector2(1.0f / image.Width, 1.0f / image.Height));
            }

            public void UpdateTexture(Image image, int deviceID, Controller controller = null)
            {
                if (deviceID >= MAX_NUMBER_OF_GLOBAL_TEXTURES)
                {
                    Debug.LogWarning("DeviceID too high: " + deviceID);
                    return;
                }

                byte[] data = image.Data(Image.CameraType.LEFT);
                if (_hideLeapDebugInfo && controller != null)
                {
                    switch (controller.Devices.ActiveDevice.Type)
                    {
                        case Device.DeviceType.TYPE_RIGEL:
                        case Device.DeviceType.TYPE_SIR170:
                        case Device.DeviceType.TYPE_3DI:
                            for (int i = 0; i < image.Width; i++)
                                data[i] = 0x00;
                            for (int i = (int)image.NumBytes - image.Width; i < image.NumBytes; i++)
                                data[i] = 0x00;
                            break;
                    }
                }

                // image data is sometimes too small for one frame when there are multiple image retrievers (why?)
                // to avoid errors, don't update the texture if that is the case
                if (_combinedTexture.GetRawTextureData().Length > data.Length)
                {
                    return;
                }

                _combinedTexture.LoadRawTextureData(data);
                _combinedTexture.Apply();

                Texture temp = Shader.GetGlobalTexture("_LeapGlobalRawTexture");

                //Texture2DArray globalRawTexture;
                if (temp == null || temp.dimension != UnityEngine.Rendering.TextureDimension.Tex2DArray || temp.width == 1)
                {
                    _globalRawTextures = new Texture2DArray(_combinedTexture.width, _combinedTexture.height, MAX_NUMBER_OF_GLOBAL_TEXTURES, _combinedTexture.format, false, true);
                    _globalRawTextures.wrapMode = TextureWrapMode.Clamp;
                    _globalRawTextures.filterMode = FilterMode.Bilinear;
                    _globalRawTextures.hideFlags = HideFlags.DontSave;
                }
                else
                {
                    _globalRawTextures = (Texture2DArray)temp;
                }

                Graphics.CopyTexture(_combinedTexture, 0, 0, 0, 0, _combinedTexture.width, _combinedTexture.height, _globalRawTextures, deviceID, 0, 0, 0);
            }

            private TextureFormat getTextureFormat(Image image)
            {
                switch (image.Format)
                {
                    case Image.FormatType.INFRARED:
                        return TextureFormat.Alpha8;
                    default:
                        throw new Exception("Unexpected image format " + image.Format + "!");
                }
            }

            private int bytesPerPixel(TextureFormat format)
            {
                switch (format)
                {
                    case TextureFormat.Alpha8:
                        return 1;
                    default:
                        throw new Exception("Unexpected texture format " + format);
                }
            }
        }

        public class LeapDistortionData
        {
            private Texture2D _combinedTexture = null;

            public Texture2D CombinedTexture
            {
                get
                {
                    return _combinedTexture;
                }
            }

            public bool CheckStale()
            {
                return _combinedTexture == null;
            }

            public void Reconstruct(Image image, string shaderName, int deviceID)
            {
                int combinedWidth = image.DistortionWidth / 2;
                int combinedHeight = image.DistortionHeight * 2;

                if (_combinedTexture != null)
                {
                    DestroyImmediate(_combinedTexture);
                }

                Color32[] colorArray = new Color32[combinedWidth * combinedHeight];
                _combinedTexture = new Texture2D(combinedWidth, combinedHeight, TextureFormat.RGBA32, false, true);
                _combinedTexture.filterMode = FilterMode.Bilinear;
                _combinedTexture.wrapMode = TextureWrapMode.Clamp;
                _combinedTexture.hideFlags = HideFlags.DontSave;

                addDistortionData(image, colorArray, 0);

                _combinedTexture.SetPixels32(colorArray);
                _combinedTexture.Apply();

                Texture2DArray globalDistortionTextures;
                Texture temp = Shader.GetGlobalTexture(shaderName);
                if (temp == null || temp.dimension != UnityEngine.Rendering.TextureDimension.Tex2DArray || temp.width == 1)
                {
                    globalDistortionTextures = new Texture2DArray(_combinedTexture.width, _combinedTexture.height, MAX_NUMBER_OF_GLOBAL_TEXTURES, _combinedTexture.format, false, true);
                    globalDistortionTextures.wrapMode = TextureWrapMode.Clamp;
                    globalDistortionTextures.filterMode = FilterMode.Bilinear;
                    globalDistortionTextures.hideFlags = HideFlags.DontSave;
                }
                else
                {
                    globalDistortionTextures = (Texture2DArray)temp;
                }

                Graphics.CopyTexture(_combinedTexture, 0, globalDistortionTextures, deviceID);

                Shader.SetGlobalTexture(shaderName, globalDistortionTextures);
            }

            private void addDistortionData(Image image, Color32[] colors, int startIndex)
            {
                float[] distortionData = image.Distortion(Image.CameraType.LEFT).
                                               Query().
                                               Concat(image.Distortion(Image.CameraType.RIGHT)).
                                               ToArray();

                for (int i = 0; i < distortionData.Length; i += 2)
                {
                    byte b0, b1, b2, b3;
                    encodeFloat(distortionData[i], out b0, out b1);
                    encodeFloat(distortionData[i + 1], out b2, out b3);
                    colors[i / 2 + startIndex] = new Color32(b0, b1, b2, b3);
                }
            }

            private void encodeFloat(float value, out byte byte0, out byte byte1)
            {
                // The distortion range is -0.6 to +1.7. Normalize to range [0..1).
                value = (value + 0.6f) / 2.3f;
                float enc_0 = value;
                float enc_1 = value * 255.0f;

                enc_0 = enc_0 - (int)enc_0;
                enc_1 = enc_1 - (int)enc_1;

                enc_0 -= 1.0f / 255.0f * enc_1;

                byte0 = (byte)(enc_0 * 256.0f);
                byte1 = (byte)(enc_1 * 256.0f);
            }
        }

        public class EyeTextureData
        {
            private const string GLOBAL_RAW_TEXTURE_NAME = "_LeapGlobalRawTexture";
            private const string GLOBAL_DISTORTION_TEXTURE_NAME = "_LeapGlobalDistortion";
            private const string GLOBAL_RAW_PIXEL_SIZE_NAME = "_LeapGlobalRawPixelSize";

            public readonly LeapTextureData TextureData;
            public readonly LeapDistortionData Distortion;
            private bool _isStale = false;

            public static void ResetGlobalShaderValues()
            {
                Texture2D empty = new Texture2D(1, 1, TextureFormat.ARGB32, false, false);
                empty.name = "EmptyTexture";
                empty.hideFlags = HideFlags.DontSave;
                empty.SetPixel(0, 0, new Color(0, 0, 0, 0));

                Shader.SetGlobalTexture(GLOBAL_RAW_TEXTURE_NAME, empty);
                Shader.SetGlobalTexture(GLOBAL_DISTORTION_TEXTURE_NAME, empty);
            }

            public EyeTextureData()
            {
                TextureData = new LeapTextureData();
                Distortion = new LeapDistortionData();
            }

            public void HideDebugInfo(bool hideDebug)
            {
                TextureData?.HideDebugInfo(hideDebug);
            }

            public bool CheckStale(Image image)
            {
                return TextureData.CheckStale(image) ||
                       Distortion.CheckStale() ||
                       _isStale;
            }

            public void MarkStale()
            {
                _isStale = true;
            }

            public void Reconstruct(Image image, int deviceID)
            {
                TextureData.Reconstruct(image, GLOBAL_RAW_TEXTURE_NAME, GLOBAL_RAW_PIXEL_SIZE_NAME, deviceID);
                Distortion.Reconstruct(image, GLOBAL_DISTORTION_TEXTURE_NAME, deviceID);
                _isStale = false;
            }

            public void UpdateTextures(Image image, int deviceID, Controller controller = null)
            {
                TextureData.UpdateTexture(image, deviceID, controller);
            }
        }

#if UNITY_EDITOR
        void OnValidate()
        {
            if (Application.isPlaying)
            {
                ApplyGammaCorrectionValues();
            }
            else
            {
                EyeTextureData.ResetGlobalShaderValues();
            }
        }
#endif

        private void Awake()
        {
            if (_provider == null)
            {
                Debug.Log("Provider not assigned");
                this.enabled = false;
                return;
            }

            Camera.onPreRender -= OnCameraPreRender;
            Camera.onPreRender += OnCameraPreRender;

            //Enable pooling to reduce overhead of images
            LeapInternal.MemoryManager.EnablePooling = true;

            ApplyGammaCorrectionValues();
#if UNITY_2019_3_OR_NEWER
            //SRP require subscribing to RenderPipelineManagers
            if(UnityEngine.Rendering.GraphicsSettings.renderPipelineAsset != null) {
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= onBeginRendering;
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += onBeginRendering;
            }
#endif
        }

        private void OnEnable()
        {
            subscribeToService();
        }

        private void OnDisable()
        {
            unsubscribeFromService();
        }

        private void OnDestroy()
        {
            StopAllCoroutines();

            Controller controller = _provider.GetLeapController();
            if (controller != null)
            {
                controller.DistortionChange -= onDistortionChange;
                controller.Disconnect -= onDisconnect;
                controller.ImageReady -= onImageReady;
                controller.FrameReady -= onFrameReady;
                _provider.OnDeviceChanged -= OnDeviceChanged;
            }

            Camera.onPreRender -= OnCameraPreRender;

#if UNITY_2019_3_OR_NEWER
            //SRP require subscribing to RenderPipelineManagers
            if (UnityEngine.Rendering.GraphicsSettings.renderPipelineAsset != null)
            {
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= onBeginRendering;
            }
#endif
        }

        private void LateUpdate()
        {
            _eyeTextureData.HideDebugInfo(HideRigelDebug);

            var xrProvider = _provider as LeapXRServiceProvider;
            if (xrProvider != null)
            {
                if (xrProvider.mainCamera == null) { return; }
            }

            Frame imageFrame = _provider.CurrentFrame;

            _currentImage = null;

            if (_needQueueReset)
            {
                while (_imageQueue.TryDequeue()) { }
                _needQueueReset = false;
            }

            /* Use the most recent image that is not newer than the current frame
             * This means that the shown image might be slightly older than the current
             * frame if for some reason a frame arrived before an image did.
             * 
             * Usually however, this is just important when robust mode is enabled.
             * At that time, image ids never line up with tracking ids.
             */
            Image potentialImage;
            while (_imageQueue.TryPeek(out potentialImage))
            {
                if (_frameIDOffset == -1) // Initialise to incoming image ID
                {
                    _frameIDOffset = potentialImage.SequenceId + 1;

                    if (_frameIDOffset != 0)
                    {
                        Debug.LogWarning("Incoming image ID was " + potentialImage.SequenceId + " but we expected zero. Compensating..");
                    }
                }
                if (potentialImage.SequenceId > imageFrame.Id)
                {
                    break;
                }

                _currentImage = potentialImage;
                _imageQueue.TryDequeue();
            }
        }

        private void OnCameraPreRender(Camera cam)
        {
            if (_currentImage != null)
            {
                if (_eyeTextureData.CheckStale(_currentImage))
                {
                    _needQueueReset = true;

                    _eyeTextureData.Reconstruct(_currentImage, (int)_provider.CurrentDevice.DeviceID);

                    // if there is a quad that renders the infrared image, set the correct deviceID on its material
                    Renderer quadRenderer = GetComponentInChildren<Renderer>();
                    if (quadRenderer != null)
                    {
                        quadRenderer.material.SetFloat("_DeviceID", _provider.CurrentDevice.DeviceID);
                    }
                }

                _eyeTextureData.UpdateTextures(_currentImage, (int)_provider.CurrentDevice.DeviceID, _provider?.GetLeapController());
            }
        }

#if UNITY_2019_3_OR_NEWER
        private void onBeginRendering(UnityEngine.Rendering.ScriptableRenderContext scriptableRenderContext, Camera camera)
        {
            OnCameraPreRender(camera);
        }
#endif

        private void subscribeToService()
        {
            if (_serviceCoroutine != null)
            {
                return;
            }

            _serviceCoroutine = StartCoroutine(serviceCoroutine());
        }

        private void unsubscribeFromService()
        {
            if (_serviceCoroutine != null)
            {
                StopCoroutine(_serviceCoroutine);
                _serviceCoroutine = null;
            }

            var controller = _provider.GetLeapController();
            if (controller != null)
            {
                controller.ClearPolicy(Controller.PolicyFlag.POLICY_IMAGES, _provider.CurrentDevice);
                controller.Disconnect -= onDisconnect;
                controller.ImageReady -= onImageReady;
                controller.DistortionChange -= onDistortionChange;
                controller.FrameReady -= onFrameReady;
                _provider.OnDeviceChanged -= OnDeviceChanged;
            }
            _eyeTextureData.MarkStale();
        }

        private Coroutine _serviceCoroutine = null;
        private IEnumerator serviceCoroutine()
        {
            Controller controller = null;
            do
            {
                controller = _provider.GetLeapController();
                yield return null;
            } while (controller == null);

            controller.FrameReady += onFrameReady;
            controller.Disconnect += onDisconnect;
            controller.ImageReady += onImageReady;
            controller.DistortionChange += onDistortionChange;
            _provider.OnDeviceChanged += OnDeviceChanged;
        }

        private void OnDeviceChanged(Device d)
        {
            Controller controller = _provider.GetLeapController();
            controller.FrameReady += onFrameReady;
        }

        private void onImageReady(object sender, ImageEventArgs args)
        {
            Image image = args.image;

            if (!_needQueueReset && !_imageQueue.TryEnqueue(image))
            {
                Debug.LogWarning("Image buffer filled up. This is unexpected and means images are being provided faster than " +
                                 "LeapImageRetriever can consume them.  This might happen if the application has stalled " +
                                 "or we recieved a very high volume of images suddenly.");
                _needQueueReset = true;
            }

            if (image.SequenceId < _prevSequenceId)
            {
                //We moved back in time, so we should reset the queue so it doesn't get stuck
                //on the previous image, which will be very old.
                //this typically happens when the service is restarted while the application is running.
                _needQueueReset = true;
            }
            _prevSequenceId = image.SequenceId;
        }

        private void onFrameReady(object sender, FrameEventArgs args)
        {
            var controller = _provider.GetLeapController();
            if (controller != null)
            {
                controller.FrameReady -= onFrameReady;
                controller.SetPolicy(Controller.PolicyFlag.POLICY_IMAGES, _provider.CurrentDevice);
            }
        }

        private void onDisconnect(object sender, ConnectionLostEventArgs args)
        {
            var controller = _provider.GetLeapController();
            if (controller != null)
            {
                controller.FrameReady += onFrameReady;
                controller.ClearPolicy(Controller.PolicyFlag.POLICY_IMAGES, _provider.CurrentDevice);
            }
        }

        public void ApplyGammaCorrectionValues()
        {
            float gamma = 1f;
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
            {
                gamma = -Mathf.Log10(Mathf.GammaToLinearSpace(0.1f));
            }
            Shader.SetGlobalFloat(GLOBAL_COLOR_SPACE_GAMMA_NAME, gamma);
            Shader.SetGlobalFloat(GLOBAL_GAMMA_CORRECTION_EXPONENT_NAME, 1.0f / _gammaCorrection);
        }

        void onDistortionChange(object sender, LeapEventArgs args)
        {
            _eyeTextureData.MarkStale();
        }
    }
}