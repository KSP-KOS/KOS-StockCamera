using System.Collections;
using kOS.Safe.Utilities;
using UnityEngine;

namespace kOS.AddOns.StockCamera
{
    internal enum FreeCameraAnchor
    {
        Body,
        Ship
    }

    internal enum FreeCameraAnchorFrame
    {
        Raw,
        Facing
    }

    internal sealed class FreeCameraController : MonoBehaviour
    {
        private const string LogPrefix = "[kOS-FreeCamera] ";
        private const string HudPrefix = "kOS FreeCamera: ";
        private const float HudMessageDuration = 3f;
        private static FreeCameraController instance;

        private SharedObjects shared;
        private FlightCamera flightCamera;
        private GameObject cameraParent;
        private CameraSnapshot originalCamera;

        private bool requestedEnabled;
        private bool active;
        private bool suspendedForCameraMode;
        private bool deferredStockRestoreUntilFlight;
        private bool eventsRegistered;

        // World-space fallback pose.  This is used before POSITION has been set,
        // and while suspending/resuming from map view.
        private bool hasDesiredPose;
        private Vector3 desiredPosition;
        private Quaternion desiredRotation;

        // POSITION is always exposed to kOS as SHIP-RAW: a raw-axis vector
        // relative to the CPU vessel CoM.  ANCHOR controls how that world pose is
        // maintained after it is set.
        private bool hasPositionState;
        private FreeCameraAnchor anchor = FreeCameraAnchor.Ship;
        private FreeCameraAnchorFrame anchorFrame = FreeCameraAnchorFrame.Raw;
        private Vessel anchorVessel;
        private Vector3 anchorOffset;
        private Vector3 anchorFacingOffset;
        private Quaternion anchorFacingRotation = Quaternion.identity;

        // BODY anchoring cannot persist a raw Unity world coordinate because KSP
        // moves the floating origin.  Store the pose in the main body's local
        // transform space instead, and resolve it back to world space each frame.
        private CelestialBody bodyAnchorBody;
        private Vector3 bodyLocalPosition;
        private Vector3 bodyFallbackWorldPosition;

        private bool hasManualRotation;
        private float heading;
        // Heading/pitch/roll are stored as normalized degrees. Pitch is intentionally not clamped;
        // allowing values outside +/-90 makes barrel-roll/loop camera paths possible.
        private float pitch;
        private float roll;

        // <= 0 means "copy the current stock camera FOV on next activation".
        private float requestedFov = 0f;
        private string status = "Stock camera active.";

        public static FreeCameraController Instance
        {
            get
            {
                if (instance == null)
                {
                    var go = new GameObject("kOS.FreeCamera.Controller");
                    instance = go.AddComponent<FreeCameraController>();
                    DontDestroyOnLoad(go);
                }

                return instance;
            }
        }

        public static bool IsFlightCameraAvailable
        {
            get
            {
                return HighLogic.LoadedSceneIsFlight &&
                       FlightCamera.fetch != null &&
                       CameraManager.Instance != null;
            }
        }

        public bool RequestedEnabled
        {
            get { return requestedEnabled; }
        }

        public bool Active
        {
            get { return active; }
        }

        public string Status
        {
            get { return status; }
        }

        public float Fov
        {
            get
            {
                if (active && flightCamera != null)
                {
                    return flightCamera.FieldOfView;
                }

                if (requestedFov > 0f)
                {
                    return requestedFov;
                }

                if (IsFlightCameraAvailable && FlightCamera.fetch != null)
                {
                    return FlightCamera.fetch.FieldOfView;
                }

                return 60f;
            }
            set
            {
                requestedFov = Mathf.Clamp(value, 1f, 179f);
                if (active)
                {
                    ApplyFov();
                }
            }
        }

        public Vector3 RelativePosition
        {
            get { return GetCurrentRelativePosition(); }
            set
            {
                var worldPosition = GetCpuOrigin() + value;
                StoreWorldPositionForCurrentAnchor(worldPosition);
                ApplyDesiredPoseToParent();
                status = "Free camera position set.";
            }
        }

        public Quaternion Orientation
        {
            get { return ResolveDesiredWorldRotation(); }
            set
            {
                // A Direction is an exact quaternion.  Keep it exact until the user
                // next sets HEADING/PITCH/ROLL, which switches back to HPR mode.
                hasManualRotation = false;
                StoreWorldRotationForCurrentAnchor(value);
                SetEulerFieldsFromRotation(value);
                ApplyDesiredPoseToParent();
                status = "Free camera orientation set.";
            }
        }

        public void SetPose(Vector3 relativePosition, Quaternion worldRotation)
        {
            // POSITION is SHIP-RAW, so the input vector is relative to the CPU vessel
            // in raw KSP axes.  Store both parts before applying to avoid a one-frame
            // intermediate pose when scripts want to change position and orientation
            // together.
            var worldPosition = GetCpuOrigin() + relativePosition;
            hasManualRotation = false;
            StoreWorldPositionForCurrentAnchor(worldPosition);
            StoreWorldRotationForCurrentAnchor(worldRotation);
            SetEulerFieldsFromRotation(worldRotation);
            ApplyDesiredPoseToParent();
            status = "Free camera pose set.";
        }

        public string Anchor
        {
            get { return anchor == FreeCameraAnchor.Ship ? "SHIP" : "BODY"; }
            set
            {
                var parsed = ParseAnchor(value);
                if (parsed == anchor)
                {
                    return;
                }

                var currentWorldPosition = ResolveDesiredWorldPosition();
                var currentWorldRotation = ResolveDesiredWorldRotation();
                anchor = parsed;
                StoreWorldPositionForCurrentAnchor(currentWorldPosition);
                StoreWorldRotationForCurrentAnchor(currentWorldRotation);
                ApplyDesiredPoseToParent();
                status = "Free camera anchor set to " + Anchor + ".";
                ShowHudMessage("Anchor: " + Anchor);
            }
        }

        public string AnchorFrame
        {
            get { return anchorFrame == FreeCameraAnchorFrame.Facing ? "FACING" : "RAW"; }
            set
            {
                var parsed = ParseAnchorFrame(value);
                if (parsed == anchorFrame)
                {
                    return;
                }

                var currentWorldPosition = ResolveDesiredWorldPosition();
                var currentWorldRotation = ResolveDesiredWorldRotation();
                anchorFrame = parsed;
                StoreWorldPositionForCurrentAnchor(currentWorldPosition);
                StoreWorldRotationForCurrentAnchor(currentWorldRotation);
                ApplyDesiredPoseToParent();
                status = "Free camera anchor frame set to " + AnchorFrame + ".";
                ShowHudMessage("Anchor frame: " + AnchorFrame);
            }
        }

        public Vessel AnchorVessel
        {
            get { return GetAnchorVessel(); }
            set
            {
                if (value == null)
                {
                    return;
                }

                var currentWorldPosition = ResolveDesiredWorldPosition();
                var currentWorldRotation = ResolveDesiredWorldRotation();
                anchorVessel = value;
                StoreWorldPositionForCurrentAnchor(currentWorldPosition);
                StoreWorldRotationForCurrentAnchor(currentWorldRotation);
                ApplyDesiredPoseToParent();
                status = "Free camera anchor vessel set to " + value.vesselName + ".";
                ShowHudMessage("Anchor vessel: " + value.vesselName);
            }
        }

        public float Heading
        {
            get { return heading; }
            set
            {
                heading = NormalizeDegrees(value);
                hasManualRotation = true;
                StoreWorldRotationForCurrentAnchor(BuildManualRotation());
                ApplyDesiredPoseToParent();
                status = "Free camera heading set.";
            }
        }

        public float Pitch
        {
            get { return pitch; }
            set
            {
                pitch = NormalizeDegrees(value);
                hasManualRotation = true;
                StoreWorldRotationForCurrentAnchor(BuildManualRotation());
                ApplyDesiredPoseToParent();
                status = "Free camera pitch set.";
            }
        }

        public float Roll
        {
            get { return roll; }
            set
            {
                roll = NormalizeDegrees(value);
                hasManualRotation = true;
                StoreWorldRotationForCurrentAnchor(BuildManualRotation());
                ApplyDesiredPoseToParent();
                status = "Free camera roll set.";
            }
        }

        public void SetSharedObjects(SharedObjects sharedObjects)
        {
            shared = sharedObjects;
            if (anchorVessel == null && shared != null && shared.Vessel != null)
            {
                anchorVessel = shared.Vessel;
            }
        }

        public void SetRequestedEnabled(bool enabled)
        {
            requestedEnabled = enabled;

            if (enabled)
            {
                TryActivateOrDefer();
            }
            else
            {
                RestoreCamera(true, "Free camera disabled by kOS.");
            }
        }

        public void CopyFromStockCamera()
        {
            if (!IsFlightCameraAvailable)
            {
                status = "Cannot copy stock camera pose because FlightCamera is unavailable.";
                return;
            }

            var cam = FlightCamera.fetch;
            if (cam == null)
            {
                status = "Cannot copy stock camera pose because FlightCamera.fetch is null.";
                return;
            }

            StoreWorldPositionForCurrentAnchor(cam.transform.position);
            hasManualRotation = false;
            StoreWorldRotationForCurrentAnchor(cam.transform.rotation);
            SetEulerFieldsFromRotation(desiredRotation);

            requestedFov = Mathf.Clamp(cam.FieldOfView, 1f, 179f);

            if (active && cameraParent != null)
            {
                cameraParent.transform.SetPositionAndRotation(ResolveDesiredWorldPosition(), desiredRotation);
                ApplyFov();
            }

            status = "Copied current stock camera pose.";
        }

        public void ResetCamera()
        {
            if (originalCamera.IsValid)
            {
                hasManualRotation = false;
                StoreWorldPositionForCurrentAnchor(originalCamera.Position);
                StoreWorldRotationForCurrentAnchor(originalCamera.Rotation);
                SetEulerFieldsFromRotation(desiredRotation);
                requestedFov = Mathf.Clamp(originalCamera.Fov, 1f, 179f);

                ApplyDesiredPoseToParent();
                ApplyFov();
                status = "Free camera reset to the saved stock camera pose.";
                return;
            }

            hasPositionState = false;
            hasManualRotation = false;
            hasDesiredPose = false;
            anchorOffset = Vector3.zero;
            anchorFacingOffset = Vector3.zero;
            anchorFacingRotation = Quaternion.identity;
            bodyAnchorBody = null;
            bodyLocalPosition = Vector3.zero;
            bodyFallbackWorldPosition = Vector3.zero;
            heading = 0f;
            pitch = 0f;
            roll = 0f;

            requestedFov = 0f;

            status = "Free camera reset.";
        }

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }

            instance = this;
            DontDestroyOnLoad(gameObject);
            RegisterEvents();
        }

        private void OnDestroy()
        {
            RestoreCamera(true, "Controller destroyed; stock camera restored.");
            UnregisterEvents();
            if (instance == this)
            {
                instance = null;
            }
        }

        private void LateUpdate()
        {
            if (deferredStockRestoreUntilFlight)
            {
                TryRunDeferredStockRestore();
            }

            if (!requestedEnabled)
            {
                return;
            }

            if (!active)
            {
                if (suspendedForCameraMode && IsFlightCameraAvailable && IsCurrentCameraMode(CameraManager.CameraMode.Flight))
                {
                    TryActivateOrDefer();
                }

                return;
            }

            if (!IsFlightCameraAvailable)
            {
                RestoreCamera(true, "FlightCamera became unavailable; stock camera restored.");
                return;
            }

            if (!IsCurrentCameraMode(CameraManager.CameraMode.Flight))
            {
                if (IsIvaCameraMode(CameraManager.Instance.currentCameraMode))
                {
                    DisableForIvaAndDeferRestore("IVA camera selected; free camera disabled.");
                }
                else
                {
                    SuspendForCameraMode("Camera mode changed away from Flight; free camera suspended.");
                }
                return;
            }

            if (flightCamera == null)
            {
                flightCamera = FlightCamera.fetch;
            }

            if (flightCamera == null || cameraParent == null)
            {
                RestoreCamera(true, "FlightCamera or camera parent disappeared; stock camera restored.");
                return;
            }

            if (flightCamera.transform.parent != cameraParent.transform)
            {
                HandleCameraParentChanged();
                if (!active)
                {
                    return;
                }
            }

            ApplyActiveCameraPose();
        }

        private bool TryActivateOrDefer()
        {
            if (!IsFlightCameraAvailable)
            {
                suspendedForCameraMode = false;
                status = "Free camera requested, but FlightCamera is not available in the current scene.";
                return false;
            }

            if (!IsCurrentCameraMode(CameraManager.CameraMode.Flight))
            {
                if (IsIvaCameraMode(CameraManager.Instance.currentCameraMode))
                {
                    requestedEnabled = false;
                    suspendedForCameraMode = false;
                    status = "Free camera cannot activate in IVA camera mode.";
                    ShowHudMessage("Cannot enable in IVA camera mode");
                    return false;
                }

                suspendedForCameraMode = true;
                status = "Free camera requested; waiting for Flight camera mode.";
                return false;
            }

            var resumeFromSuspension = suspendedForCameraMode && originalCamera.IsValid;
            return ActivateNow(!resumeFromSuspension);
        }

        private bool ActivateNow(bool captureOriginalCamera)
        {
            flightCamera = FlightCamera.fetch;
            if (flightCamera == null)
            {
                status = "Cannot activate free camera because FlightCamera.fetch is null.";
                return false;
            }

            if (active)
            {
                return true;
            }

            if (captureOriginalCamera || !originalCamera.IsValid)
            {
                originalCamera = CameraSnapshot.Capture(flightCamera);
            }

            if (!hasDesiredPose)
            {
                StoreWorldPositionForCurrentAnchor(flightCamera.transform.position);
                StoreWorldRotationForCurrentAnchor(flightCamera.transform.rotation);
                SetEulerFieldsFromRotation(desiredRotation);
            }

            if (requestedFov <= 0f)
            {
                requestedFov = Mathf.Clamp(originalCamera.Fov, 1f, 179f);
            }

            if (cameraParent == null)
            {
                cameraParent = new GameObject("kOS.FreeCamera.CameraParent");
                DontDestroyOnLoad(cameraParent);
            }

            cameraParent.transform.SetPositionAndRotation(ResolveDesiredWorldPosition(), ResolveDesiredWorldRotation());

            flightCamera.SetTargetNone();
            flightCamera.transform.parent = cameraParent.transform;
            flightCamera.transform.localPosition = Vector3.zero;
            flightCamera.transform.localRotation = Quaternion.identity;
            flightCamera.mode = FlightCamera.Modes.FREE;
            flightCamera.DeactivateUpdate();

            active = true;
            suspendedForCameraMode = false;
            ApplyFov();

            status = "Free camera active.";
            UnityEngine.Debug.Log(LogPrefix + status);
            ShowHudMessage("Enabled");
            return true;
        }

        private void ApplyFov()
        {
            if (flightCamera == null)
            {
                return;
            }

            var fov = Mathf.Clamp(requestedFov, 1f, 179f);
            flightCamera.FieldOfView = fov;
            flightCamera.SetFoV(fov);
        }

        private void ApplyActiveCameraPose()
        {
            if (!active || flightCamera == null || cameraParent == null)
            {
                return;
            }

            ApplyDesiredPoseToParent();

            if (flightCamera.Target != null)
            {
                flightCamera.SetTargetNone();
            }

            flightCamera.transform.localPosition = Vector3.zero;
            flightCamera.transform.localRotation = Quaternion.identity;
            flightCamera.DeactivateUpdate();
            ApplyFov();
        }

        private void ApplyDesiredPoseToParent()
        {
            if (!active || cameraParent == null)
            {
                return;
            }

            var worldPosition = ResolveDesiredWorldPosition();
            var worldRotation = ResolveDesiredWorldRotation();
            cameraParent.transform.SetPositionAndRotation(worldPosition, worldRotation);

            // Keep the world fallback pose current so map-view suspension can resume
            // where the free camera was before entering map mode.
            desiredPosition = worldPosition;
            desiredRotation = worldRotation;
            hasDesiredPose = true;
        }

        private Vector3 ResolveDesiredWorldPosition()
        {
            if (hasPositionState)
            {
                if (anchor == FreeCameraAnchor.Ship)
                {
                    var vessel = GetAnchorVessel();
                    if (vessel != null)
                    {
                        var origin = GetVesselOrigin(vessel);
                        if (anchorFrame == FreeCameraAnchorFrame.Facing)
                        {
                            return origin + (GetVesselFacingRotation(vessel) * anchorFacingOffset);
                        }

                        return origin + anchorOffset;
                    }
                }

                var resolvedBodyPosition = ResolveBodyAnchoredWorldPosition();
                if (resolvedBodyPosition.HasValue)
                {
                    return resolvedBodyPosition.Value;
                }

                return bodyFallbackWorldPosition;
            }

            if (hasDesiredPose)
            {
                return desiredPosition;
            }

            if (cameraParent != null)
            {
                return cameraParent.transform.position;
            }

            if (FlightCamera.fetch != null)
            {
                return FlightCamera.fetch.transform.position;
            }

            return Vector3.zero;
        }

        private Quaternion ResolveDesiredWorldRotation()
        {
            if (anchor == FreeCameraAnchor.Ship && anchorFrame == FreeCameraAnchorFrame.Facing && hasDesiredPose)
            {
                var vessel = GetAnchorVessel();
                if (vessel != null)
                {
                    return GetVesselFacingRotation(vessel) * anchorFacingRotation;
                }
            }

            if (hasManualRotation)
            {
                return BuildManualRotation();
            }

            if (hasDesiredPose)
            {
                return desiredRotation;
            }

            if (cameraParent != null)
            {
                return cameraParent.transform.rotation;
            }

            if (FlightCamera.fetch != null)
            {
                return FlightCamera.fetch.transform.rotation;
            }

            return Quaternion.identity;
        }

        private Vector3 GetCurrentRelativePosition()
        {
            var worldPosition = ResolveDesiredWorldPosition();

            if (active && cameraParent != null)
            {
                worldPosition = cameraParent.transform.position;
            }
            else if (!hasPositionState && !hasDesiredPose && IsFlightCameraAvailable && FlightCamera.fetch != null)
            {
                worldPosition = FlightCamera.fetch.transform.position;
            }

            return worldPosition - GetCpuOrigin();
        }

        private void StoreWorldPositionForCurrentAnchor(Vector3 worldPosition)
        {
            hasPositionState = true;

            // Always keep both anchor representations current.  This lets changing
            // ANCHOR preserve the current visual position without teleporting.
            StoreBodyAnchoredWorldPosition(worldPosition);

            var vessel = GetAnchorVessel();
            if (vessel != null)
            {
                anchorOffset = worldPosition - GetVesselOrigin(vessel);
                anchorFacingOffset = Quaternion.Inverse(GetVesselFacingRotation(vessel)) * anchorOffset;
            }
            else
            {
                anchorOffset = worldPosition - GetCpuOrigin();
                anchorFacingOffset = anchorOffset;
            }

            desiredPosition = worldPosition;
            hasDesiredPose = true;
        }

        private void StoreWorldRotationForCurrentAnchor(Quaternion worldRotation)
        {
            desiredRotation = worldRotation;
            hasDesiredPose = true;

            var vessel = GetAnchorVessel();
            if (vessel != null)
            {
                anchorFacingRotation = Quaternion.Inverse(GetVesselFacingRotation(vessel)) * worldRotation;
            }
            else
            {
                anchorFacingRotation = worldRotation;
            }
        }

        private void StoreBodyAnchoredWorldPosition(Vector3 worldPosition)
        {
            bodyFallbackWorldPosition = worldPosition;

            var body = GetAnchorBody();
            if (body != null && body.transform != null)
            {
                bodyAnchorBody = body;
                bodyLocalPosition = body.transform.InverseTransformPoint(worldPosition);
            }
        }

        private Vector3? ResolveBodyAnchoredWorldPosition()
        {
            if (bodyAnchorBody != null && bodyAnchorBody.transform != null)
            {
                return bodyAnchorBody.transform.TransformPoint(bodyLocalPosition);
            }

            return null;
        }

        private Vector3 GetCpuOrigin()
        {
            var vessel = GetCpuVessel();
            return vessel != null ? GetVesselOrigin(vessel) : Vector3.zero;
        }

        private Vessel GetCpuVessel()
        {
            if (shared != null && shared.Vessel != null)
            {
                return shared.Vessel;
            }

            return FlightGlobals.ActiveVessel;
        }

        private CelestialBody GetAnchorBody()
        {
            var vessel = GetCpuVessel();
            if (vessel != null && vessel.mainBody != null)
            {
                return vessel.mainBody;
            }

            if (FlightGlobals.ActiveVessel != null)
            {
                return FlightGlobals.ActiveVessel.mainBody;
            }

            return null;
        }

        private Vessel GetAnchorVessel()
        {
            if (IsUsableVessel(anchorVessel))
            {
                return anchorVessel;
            }

            anchorVessel = null;

            var vessel = GetCpuVessel();
            if (IsUsableVessel(vessel))
            {
                anchorVessel = vessel;
            }

            return anchorVessel;
        }

        private static bool IsUsableVessel(Vessel vessel)
        {
            return vessel != null && vessel.mainBody != null;
        }

        private static Vector3 GetVesselOrigin(Vessel vessel)
        {
            if (vessel == null)
            {
                return Vector3.zero;
            }

            // Use Vessel.CoM rather than CoMD for active/loaded vessels.  CoMD is a
            // double-precision physics value and can make the render camera appear
            // a frame behind fast-moving craft.  CoM is the scene-space center of
            // mass KSP exposes as a Vector3 and tracks the rendered vessel better.
            if (vessel.loaded)
            {
                return vessel.CoM;
            }

            var worldPos = vessel.GetWorldPos3D();
            return new Vector3((float)worldPos.x, (float)worldPos.y, (float)worldPos.z);
        }

        private static Quaternion GetVesselFacingRotation(Vessel vessel)
        {
            if (vessel == null)
            {
                return Quaternion.identity;
            }

            // Prefer the reference transform because it is the transform KSP uses for
            // the vessel's current control orientation.  kOS applies this same -90 deg X
            // correction when mapping KSP's vessel reference transform axes to the kOS
            // facing convention: fore = +Z, top = +Y, starboard = +X.
            if (vessel.ReferenceTransform != null)
            {
                return vessel.ReferenceTransform.rotation * Quaternion.Euler(-90f, 0f, 0f);
            }

            if (vessel.transform != null)
            {
                return vessel.transform.rotation * Quaternion.Euler(-90f, 0f, 0f);
            }

            return Quaternion.identity;
        }

        private Quaternion BuildManualRotation()
        {
            Vector3 origin = GetCpuOrigin();
            Vector3 surfaceUp;
            Vector3 surfaceNorth;
            Vector3 surfaceEast;
            GetStableReferenceFrame(origin, out surfaceUp, out surfaceNorth, out surfaceEast);

            // Heading is measured on the local horizon: 0 = north, +90 = east.
            Vector3 flatForward = Quaternion.AngleAxis(heading, surfaceUp) * surfaceNorth;
            if (flatForward.sqrMagnitude < 0.000001f)
            {
                flatForward = surfaceNorth;
            }
            flatForward.Normalize();

            // Positive pitch means look upward from the horizon.
            Vector3 cameraRight = Vector3.Cross(surfaceUp, flatForward);
            if (cameraRight.sqrMagnitude < 0.000001f)
            {
                cameraRight = surfaceEast;
            }
            cameraRight.Normalize();

            Quaternion pitchRotation = Quaternion.AngleAxis(-pitch, cameraRight);
            Vector3 forward = pitchRotation * flatForward;
            Vector3 up = pitchRotation * surfaceUp;

            // Roll is around the camera's own forward axis. Positive kOS roll is defined
            // opposite Unity's positive angle here because this matched in-game expectations.
            Quaternion rollRotation = Quaternion.AngleAxis(-roll, forward);
            up = rollRotation * up;

            return Quaternion.LookRotation(forward, up);
        }

        private void SetEulerFieldsFromRotation(Quaternion rotation)
        {
            Vector3 origin = GetCpuOrigin();
            Vector3 surfaceUp;
            Vector3 surfaceNorth;
            Vector3 surfaceEast;
            GetStableReferenceFrame(origin, out surfaceUp, out surfaceNorth, out surfaceEast);

            Vector3 forward = rotation * Vector3.forward;
            Vector3 cameraUp = rotation * Vector3.up;

            Vector3 flatForward = Vector3.ProjectOnPlane(forward, surfaceUp);
            if (flatForward.sqrMagnitude < 0.000001f)
            {
                flatForward = surfaceNorth;
            }
            flatForward.Normalize();

            heading = NormalizeDegrees(Mathf.Atan2(
                Vector3.Dot(flatForward, surfaceEast),
                Vector3.Dot(flatForward, surfaceNorth)) * Mathf.Rad2Deg);

            pitch = Mathf.Asin(Mathf.Clamp(Vector3.Dot(forward.normalized, surfaceUp), -1f, 1f)) * Mathf.Rad2Deg;

            // Compute roll relative to the zero-roll orientation for the current heading/pitch.
            roll = 0f;
            Quaternion noRollRotation = BuildManualRotation();
            Vector3 zeroRollUp = noRollRotation * Vector3.up;
            roll = NormalizeDegrees(-SignedAngle(zeroRollUp, cameraUp, forward));
        }

        private void GetStableReferenceFrame(Vector3 origin, out Vector3 up, out Vector3 north, out Vector3 east)
        {
            var vessel = GetCpuVessel();
            var body = vessel != null ? vessel.mainBody : null;

            if (body != null)
            {
                Vector3 bodyPosition = body.transform.position;
                up = origin - bodyPosition;
                if (up.sqrMagnitude < 0.000001f)
                {
                    up = Vector3.up;
                }
                up.Normalize();

                // KSP body transform.up points along the body's north pole axis.
                north = Vector3.ProjectOnPlane(body.transform.up, up);
                if (north.sqrMagnitude < 0.000001f)
                {
                    north = Vector3.ProjectOnPlane(body.transform.forward, up);
                }
                if (north.sqrMagnitude < 0.000001f)
                {
                    north = Vector3.ProjectOnPlane(Vector3.forward, up);
                }
                if (north.sqrMagnitude < 0.000001f)
                {
                    north = Vector3.ProjectOnPlane(Vector3.right, up);
                }
                north.Normalize();

                east = Vector3.Cross(up, north);
                if (east.sqrMagnitude < 0.000001f)
                {
                    east = Vector3.right;
                }
                east.Normalize();

                // Re-orthogonalize north so tiny numerical errors do not accumulate.
                north = Vector3.Cross(east, up).normalized;
                return;
            }

            up = Vector3.up;
            north = Vector3.forward;
            east = Vector3.right;
        }

        private static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
        {
            from.Normalize();
            to.Normalize();
            axis.Normalize();

            float unsignedAngle = Mathf.Acos(Mathf.Clamp(Vector3.Dot(from, to), -1f, 1f)) * Mathf.Rad2Deg;
            float sign = Mathf.Sign(Vector3.Dot(axis, Vector3.Cross(from, to)));
            if (Mathf.Approximately(sign, 0f))
            {
                sign = 1f;
            }

            return unsignedAngle * sign;
        }

        private static FreeCameraAnchor ParseAnchor(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return FreeCameraAnchor.Ship;
            }

            switch (value.Trim().ToUpperInvariant())
            {
                case "SHIP":
                case "VESSEL":
                case "ANCHORVESSEL":
                    return FreeCameraAnchor.Ship;
                case "BODY":
                case "WORLD":
                case "FIXED":
                    return FreeCameraAnchor.Body;
                default:
                    throw new System.ArgumentException("FREECAM:ANCHOR must be SHIP or BODY.");
            }
        }

        private static FreeCameraAnchorFrame ParseAnchorFrame(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return FreeCameraAnchorFrame.Raw;
            }

            switch (value.Trim().ToUpperInvariant())
            {
                case "RAW":
                case "SHIPRAW":
                case "WORLD":
                    return FreeCameraAnchorFrame.Raw;
                case "FACING":
                case "VESSELFACING":
                case "LOCAL":
                case "SHIPLOCAL":
                    return FreeCameraAnchorFrame.Facing;
                default:
                    throw new System.ArgumentException("FREECAM:ANCHORFRAME must be RAW or FACING.");
            }
        }

        private static float NormalizeDegrees(float value)
        {
            value = value % 360f;
            if (value > 180f)
            {
                value -= 360f;
            }
            else if (value <= -180f)
            {
                value += 360f;
            }

            return value;
        }

        private void SuspendForCameraMode(string reason)
        {
            if (!active)
            {
                suspendedForCameraMode = true;
                status = reason;
                return;
            }

            SaveCurrentFreeCameraPose();
            RestoreCamera(false, reason);
            suspendedForCameraMode = true;
        }

        private void DisableForIvaAndDeferRestore(string reason)
        {
            bool shouldShowDisableHud = active || requestedEnabled || suspendedForCameraMode;

            SaveCurrentFreeCameraPose();

            requestedEnabled = false;
            suspendedForCameraMode = false;
            deferredStockRestoreUntilFlight = originalCamera.IsValid && flightCamera != null;
            active = false;
            status = deferredStockRestoreUntilFlight
                ? reason + " FlightCamera restore deferred until Flight mode returns."
                : reason;
            UnityEngine.Debug.Log(LogPrefix + status);

            if (shouldShowDisableHud)
            {
                ShowHudForDisabledReason(reason);
            }
        }

        private void SaveCurrentFreeCameraPose()
        {
            if (cameraParent == null)
            {
                return;
            }

            StoreWorldPositionForCurrentAnchor(cameraParent.transform.position);
            StoreWorldRotationForCurrentAnchor(cameraParent.transform.rotation);
        }

        private void TryRunDeferredStockRestore()
        {
            if (!deferredStockRestoreUntilFlight)
            {
                return;
            }

            if (!HighLogic.LoadedSceneIsFlight ||
                CameraManager.Instance == null ||
                CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight)
            {
                return;
            }

            try
            {
                RestoreStockCameraFromSnapshot();
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning(LogPrefix + "Exception while running deferred stock camera restore: " + ex);
            }
            finally
            {
                deferredStockRestoreUntilFlight = false;
                active = false;
                originalCamera = new CameraSnapshot();
                status = "Stock camera restored after IVA.";
                UnityEngine.Debug.Log(LogPrefix + status);
            }
        }

        private void RestoreStockCameraFromSnapshot()
        {
            if (flightCamera == null)
            {
                flightCamera = FlightCamera.fetch;
            }

            if (flightCamera == null || !originalCamera.IsValid)
            {
                return;
            }

            RestoreStockTarget();

            if (originalCamera.Parent != null)
            {
                flightCamera.transform.parent = originalCamera.Parent;
                flightCamera.transform.localPosition = originalCamera.LocalPosition;
                flightCamera.transform.localRotation = originalCamera.LocalRotation;
                flightCamera.SetDistanceImmediate(originalCamera.Distance);
            }
            else
            {
                flightCamera.transform.parent = null;
                flightCamera.transform.position = originalCamera.Position;
                flightCamera.transform.rotation = originalCamera.Rotation;
            }

            flightCamera.mode = originalCamera.Mode;
            flightCamera.FieldOfView = originalCamera.Fov;
            flightCamera.SetFoV(originalCamera.Fov);

            if (flightCamera.mainCamera != null)
            {
                flightCamera.mainCamera.nearClipPlane = originalCamera.NearClip;
            }

            flightCamera.ActivateUpdate();
        }

        private void RestoreCamera(bool clearRequest, string reason)
        {
            bool shouldShowDisableHud = clearRequest && (active || requestedEnabled || suspendedForCameraMode);

            if (clearRequest)
            {
                requestedEnabled = false;
                suspendedForCameraMode = false;
            }

            if (!active)
            {
                status = reason;
                if (clearRequest)
                {
                    if (!deferredStockRestoreUntilFlight)
                    {
                        originalCamera = new CameraSnapshot();
                    }

                    if (shouldShowDisableHud)
                    {
                        ShowHudForDisabledReason(reason);
                    }
                }
                return;
            }

            try
            {
                if (flightCamera == null)
                {
                    flightCamera = FlightCamera.fetch;
                }

                RestoreStockCameraFromSnapshot();
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning(LogPrefix + "Exception while restoring stock camera: " + ex);
            }
            finally
            {
                active = false;
                status = reason;
                UnityEngine.Debug.Log(LogPrefix + status);

                if (clearRequest)
                {
                    deferredStockRestoreUntilFlight = false;
                    originalCamera = new CameraSnapshot();
                    if (shouldShowDisableHud)
                    {
                        ShowHudForDisabledReason(reason);
                    }
                }
            }
        }

        private void RestoreStockTarget()
        {
            if (flightCamera == null || !HighLogic.LoadedSceneIsFlight)
            {
                return;
            }

            var targetVessel = originalCamera.VesselTarget;

            if (targetVessel == null || !targetVessel.loaded)
            {
                targetVessel = FlightGlobals.ActiveVessel;
            }

            if (targetVessel == null && shared != null)
            {
                targetVessel = shared.Vessel;
            }

            if (targetVessel == null || targetVessel.transform == null)
            {
                UnityEngine.Debug.LogWarning(LogPrefix + "Could not restore FlightCamera target because no vessel target was available.");
                return;
            }

            if (flightCamera.vesselTarget != targetVessel)
            {
                flightCamera.SetTarget(targetVessel.transform, FlightCamera.TargetMode.Vessel);
            }
        }

        private void HandleCameraParentChanged()
        {
            if (flightCamera == null || cameraParent == null)
            {
                RestoreCamera(true, "Cannot repair camera parent because required objects are missing.");
                return;
            }

            if (originalCamera.IsValid && flightCamera.transform.parent == originalCamera.Parent)
            {
                flightCamera.transform.parent = cameraParent.transform;
                flightCamera.transform.localPosition = Vector3.zero;
                flightCamera.transform.localRotation = Quaternion.identity;
                flightCamera.DeactivateUpdate();
                status = "FlightCamera parent was reset by KSP; free camera reattached.";
                UnityEngine.Debug.Log(LogPrefix + status);
                return;
            }

            RestoreCamera(true, "Another system changed FlightCamera parent; free camera disabled to avoid leaving the stock camera broken.");
        }

        private void ClearFreeCameraStateForNewFlight(string reason)
        {
            active = false;
            requestedEnabled = false;
            suspendedForCameraMode = false;
            deferredStockRestoreUntilFlight = false;
            flightCamera = null;
            shared = null;
            originalCamera = new CameraSnapshot();

            hasDesiredPose = false;
            desiredPosition = Vector3.zero;
            desiredRotation = Quaternion.identity;

            hasPositionState = false;
            anchor = FreeCameraAnchor.Ship;
            anchorFrame = FreeCameraAnchorFrame.Raw;
            anchorVessel = null;
            anchorOffset = Vector3.zero;
            anchorFacingOffset = Vector3.zero;
            anchorFacingRotation = Quaternion.identity;

            bodyAnchorBody = null;
            bodyLocalPosition = Vector3.zero;
            bodyFallbackWorldPosition = Vector3.zero;

            hasManualRotation = false;
            heading = 0f;
            pitch = 0f;
            roll = 0f;

            requestedFov = 0f;

            if (cameraParent != null)
            {
                Destroy(cameraParent);
                cameraParent = null;
            }

            status = reason;
            UnityEngine.Debug.Log(LogPrefix + status);
        }


        private static bool IsCurrentCameraMode(CameraManager.CameraMode mode)
        {
            return CameraManager.Instance != null && CameraManager.Instance.currentCameraMode == mode;
        }

        private static bool IsIvaCameraMode(CameraManager.CameraMode mode)
        {
            var name = mode.ToString();
            return name.IndexOf("IVA", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                   name.IndexOf("Internal", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void ShowHudForDisabledReason(string reason)
        {
            if (string.IsNullOrEmpty(reason))
            {
                ShowHudMessage("Disabled");
                return;
            }

            if (reason.IndexOf("IVA", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ShowHudMessage("Disabled for IVA camera");
                return;
            }

            if (reason.IndexOf("disabled", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("restored", System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ShowHudMessage("Disabled");
            }
        }

        private static void ShowHudMessage(string message)
        {
            try
            {
                ScreenMessages.PostScreenMessage(HudPrefix + message, HudMessageDuration, ScreenMessageStyle.UPPER_CENTER);
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogWarning(LogPrefix + "Unable to post HUD message: " + ex.Message);
            }
        }

        private void OnCameraChange(CameraManager.CameraMode mode)
        {
            if (mode != CameraManager.CameraMode.Flight && active)
            {
                if (IsIvaCameraMode(mode))
                {
                    DisableForIvaAndDeferRestore("IVA camera selected; free camera disabled.");
                }
                else
                {
                    SuspendForCameraMode("Camera mode changed away from Flight; free camera suspended.");
                }
                return;
            }

            if (mode == CameraManager.CameraMode.Flight && requestedEnabled && suspendedForCameraMode)
            {
                StartCoroutine(ActivateAfterKspSettles());
            }
        }

        private IEnumerator ActivateAfterKspSettles()
        {
            // Give KSP a couple of frames after map-view exit before retaking FlightCamera.
            yield return null;
            yield return new WaitForEndOfFrame();
            if (requestedEnabled && suspendedForCameraMode)
            {
                TryActivateOrDefer();
            }
        }

        private void OnGameSceneLoadRequested(GameScenes scene)
        {
            RestoreCamera(true, "Scene change requested; stock camera restored.");
            ClearFreeCameraStateForNewFlight("Scene change requested; free camera pose state cleared.");
        }

        private void RegisterEvents()
        {
            if (eventsRegistered)
            {
                return;
            }

            GameEvents.OnCameraChange.Add(OnCameraChange);
            GameEvents.onGameSceneLoadRequested.Add(OnGameSceneLoadRequested);
            eventsRegistered = true;
        }

        private void UnregisterEvents()
        {
            if (!eventsRegistered)
            {
                return;
            }

            GameEvents.OnCameraChange.Remove(OnCameraChange);
            GameEvents.onGameSceneLoadRequested.Remove(OnGameSceneLoadRequested);
            eventsRegistered = false;
        }
    }
}
