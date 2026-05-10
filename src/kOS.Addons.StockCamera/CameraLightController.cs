using UnityEngine;

namespace kOS.AddOns.StockCamera
{
    internal sealed class CameraLightController : MonoBehaviour
    {
        private const string LogPrefix = "[kOS-CameraLight] ";
        private static CameraLightController instance;

        private GameObject lightObject;
        private Light cameraLight;
        private bool requestedEnabled;
        private bool eventRegistered;
        private string status = "Camera light disabled.";

        private float intensity = 1f;
        private float range = 100f;
        private float angle = 60f;
        private float distance = 1.0f;
        private float red = 1f;
        private float green = 0.92f;
        private float blue = 0.82f;
        private bool shadowsEnabled;

        public static CameraLightController Instance
        {
            get
            {
                if (instance == null)
                {
                    var go = new GameObject("kOS.CameraLight.Controller");
                    instance = go.AddComponent<CameraLightController>();
                    DontDestroyOnLoad(go);
                }

                return instance;
            }
        }

        public bool RequestedEnabled
        {
            get { return requestedEnabled; }
        }

        public bool Active
        {
            get { return cameraLight != null && cameraLight.enabled; }
        }

        public bool Available
        {
            get { return FindCurrentGameplayCamera() != null; }
        }

        public string Status
        {
            get { return status; }
        }

        public float Intensity
        {
            get { return intensity; }
            set
            {
                intensity = value;
                ApplyLightSettings();
            }
        }

        public float Range
        {
            get { return range; }
            set
            {
                range = value;
                ApplyLightSettings();
            }
        }

        public float Angle
        {
            get { return angle; }
            set
            {
                angle = value;
                ApplyLightSettings();
            }
        }

        public float Distance
        {
            get { return distance; }
            set { distance = value; }
        }

        public float Red
        {
            get { return red; }
            set
            {
                red = value;
                ApplyLightSettings();
            }
        }

        public float Green
        {
            get { return green; }
            set
            {
                green = value;
                ApplyLightSettings();
            }
        }

        public float Blue
        {
            get { return blue; }
            set
            {
                blue = value;
                ApplyLightSettings();
            }
        }

        public bool ShadowsEnabled
        {
            get { return shadowsEnabled; }
            set
            {
                shadowsEnabled = value;
                ApplyLightSettings();
            }
        }

        private void Awake()
        {
            RegisterEvents();
        }

        private void OnDestroy()
        {
            UnregisterEvents();
            if (lightObject != null)
            {
                Destroy(lightObject);
                lightObject = null;
                cameraLight = null;
            }

            if (instance == this)
            {
                instance = null;
            }
        }

        public void SetRequestedEnabled(bool enabled)
        {
            requestedEnabled = enabled;

            if (requestedEnabled)
            {
                RegisterEvents();
                EnsureLightExists();
                cameraLight.enabled = false;
                status = "Camera light enabled; waiting for a gameplay camera.";
                return;
            }

            if (cameraLight != null)
            {
                cameraLight.enabled = false;
            }

            status = "Camera light disabled.";
        }

        public void SetColor(float newRed, float newGreen, float newBlue)
        {
            red = newRed;
            green = newGreen;
            blue = newBlue;
            ApplyLightSettings();
        }

        private void OnCameraPreCull(Camera renderingCamera)
        {
            if (!requestedEnabled)
            {
                if (cameraLight != null)
                {
                    cameraLight.enabled = false;
                }

                return;
            }

            if (!IsUsableGameplayCamera(renderingCamera))
            {
                return;
            }

            EnsureLightExists();
            ApplyLightSettings();
            MoveLightBehindCamera(renderingCamera);
            cameraLight.enabled = true;
            status = "Camera light active.";
        }

        private void MoveLightBehindCamera(Camera renderingCamera)
        {
            var cameraTransform = renderingCamera.transform;
            var lightTransform = cameraLight.transform;
            lightTransform.position = cameraTransform.position - (cameraTransform.forward * distance);
            lightTransform.rotation = cameraTransform.rotation;
        }

        private void EnsureLightExists()
        {
            if (cameraLight != null)
            {
                return;
            }

            lightObject = new GameObject("kOS.CameraLight.Spot");
            DontDestroyOnLoad(lightObject);
            cameraLight = lightObject.AddComponent<Light>();
            cameraLight.type = LightType.Spot;
            cameraLight.enabled = false;
            ApplyLightSettings();
        }

        private void ApplyLightSettings()
        {
            if (cameraLight == null)
            {
                return;
            }

            cameraLight.type = LightType.Spot;
            cameraLight.intensity = intensity;
            cameraLight.range = range;
            cameraLight.spotAngle = angle;
            cameraLight.color = new Color(red, green, blue, 1f);
            cameraLight.shadows = shadowsEnabled ? LightShadows.Soft : LightShadows.None;
        }

        private bool IsUsableGameplayCamera(Camera renderingCamera)
        {
            if (renderingCamera == null || !renderingCamera.enabled || !renderingCamera.gameObject.activeInHierarchy)
            {
                return false;
            }

            if (IsKnownFlightCamera(renderingCamera) || IsKnownMapCamera(renderingCamera) || IsKnownInternalCamera(renderingCamera))
            {
                return true;
            }

            return HighLogic.LoadedSceneIsFlight && renderingCamera == Camera.main && !renderingCamera.orthographic;
        }

        private static bool IsKnownFlightCamera(Camera renderingCamera)
        {
            return FlightCamera.fetch != null &&
                   FlightCamera.fetch.mainCamera != null &&
                   renderingCamera == FlightCamera.fetch.mainCamera;
        }

        private static bool IsKnownMapCamera(Camera renderingCamera)
        {
            if (PlanetariumCamera.fetch == null)
            {
                return false;
            }

            var mapCamera = PlanetariumCamera.fetch.GetComponent<Camera>();
            return mapCamera != null && renderingCamera == mapCamera;
        }

        private static bool IsKnownInternalCamera(Camera renderingCamera)
        {
            if (InternalCamera.Instance == null)
            {
                return false;
            }

            var internalCamera = InternalCamera.Instance.GetComponentInChildren<Camera>();
            return internalCamera != null && renderingCamera == internalCamera;
        }

        private Camera FindCurrentGameplayCamera()
        {
            if (FlightCamera.fetch != null && FlightCamera.fetch.mainCamera != null)
            {
                return FlightCamera.fetch.mainCamera;
            }

            if (PlanetariumCamera.fetch != null)
            {
                var mapCamera = PlanetariumCamera.fetch.GetComponent<Camera>();
                if (mapCamera != null)
                {
                    return mapCamera;
                }
            }

            if (InternalCamera.Instance != null)
            {
                var internalCamera = InternalCamera.Instance.GetComponentInChildren<Camera>();
                if (internalCamera != null)
                {
                    return internalCamera;
                }
            }

            return Camera.main;
        }

        private void RegisterEvents()
        {
            if (!eventRegistered)
            {
                Camera.onPreCull += OnCameraPreCull;
                eventRegistered = true;
            }
        }

        private void UnregisterEvents()
        {
            if (eventRegistered)
            {
                Camera.onPreCull -= OnCameraPreCull;
                eventRegistered = false;
            }
        }
    }
}
