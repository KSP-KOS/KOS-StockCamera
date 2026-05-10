using System.Collections;
using kOS.Safe.Utilities;
using UnityEngine;

namespace kOS.AddOns.StockCamera
{
    internal enum FreeCameraAnchor
    {
        Body,
        Part,
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
        private bool cameraRenderEventRegistered;

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
        private Part anchorPart;
        private Vector3 anchorOffset;
        private Vector3 anchorFacingOffset;
        private Quaternion anchorFacingRotation = Quaternion.identity;
        private Vector3 partLocalPosition;
        private Quaternion partLocalRotation = Quaternion.identity;
        private Vector3 partFallbackWorldPosition;
        private Quaternion partFallbackWorldRotation = Quaternion.identity;

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
                requestedFov = Mathf.Clamp(value, 0.001f, 179f);
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

        public float Distance
        {
            get { return GetCurrentRelativePosition().magnitude; }
            set
            {
                var relativePosition = GetCurrentRelativePosition();
                var scaledPosition = relativePosition.normalized * Mathf.Max(0f, value);
                var worldPosition = GetCpuOrigin() + scaledPosition;
                StoreWorldPositionForCurrentAnchor(worldPosition);
                ApplyDesiredPoseToParent();
                status = "Free camera distance set.";
            }
        }

        public Quaternion Facing
        {
            get { return ResolveDesiredWorldRotation(); }
            set
            {
                // A Direction is an exact quaternion. Keep it exact until the user
                // next sets HEADING/PITCH/ROLL, which switches back to HPR mode.
                hasManualRotation = false;
                StoreWorldRotationForCurrentAnchor(value);
                SetEulerFieldsFromRotation(value);
                ApplyDesiredPoseToParent();
                status = "Free camera facing set.";
            }
        }

        /// <summary>
        /// kOS vector-facing assignments are direction vectors, not target points.
        /// Resolve them using the camera's body-up vector so the result matches
        /// KerboScript lookdirup(vector, cam:position - body:position) behavior.
        /// </summary>
        /// <param name="facingVector">World-space look direction, not a target point.</param>
        public void SetFacingVector(Vector3 facingVector)
        {
            Facing = BuildFacingRotationFromVector(facingVector, ResolveDesiredWorldPosition());
        }

        /// <summary>
        /// Atomically stores POSITION and FACING.  This avoids rendering a one-frame
        /// intermediate pose when a script changes both values in the same tick.
        /// </summary>
        /// <param name="relativePosition">Camera position relative to the CPU vessel in SHIP-RAW axes.</param>
        /// <param name="worldRotation">Exact world-space camera rotation to store.</param>
        public void SetPose(Vector3 relativePosition, Quaternion worldRotation)
        {
            // POSITION is SHIP-RAW, so the input vector is relative to the CPU vessel
            // in raw KSP axes.  Store both parts before applying to avoid a one-frame
            // intermediate pose when scripts want to change position and facing
            // together.
            var worldPosition = GetCpuOrigin() + relativePosition;
            hasManualRotation = false;
            StoreWorldPositionForCurrentAnchor(worldPosition);
            StoreWorldRotationForCurrentAnchor(worldRotation);
            SetEulerFieldsFromRotation(worldRotation);
            ApplyDesiredPoseToParent();
            status = "Free camera pose set.";
        }

        /// <summary>
        /// Vector-facing SETPOSE uses the supplied new position when calculating
        /// camera body-up, so roll/up semantics match the pose being requested rather
        /// than the camera's previous position.
        /// </summary>
        /// <param name="relativePosition">Camera position relative to the CPU vessel in SHIP-RAW axes.</param>
        /// <param name="facingVector">World-space look direction, not a target point.</param>
        public void SetPose(Vector3 relativePosition, Vector3 facingVector)
        {
            var worldPosition = GetCpuOrigin() + relativePosition;
            SetPose(relativePosition, BuildFacingRotationFromVector(facingVector, worldPosition));
        }

        /// <summary>
        /// LOOKAT takes a SHIP-RAW target point and changes facing only.  Position and
        /// anchor state are preserved; body-up at the current camera position supplies
        /// the effective up vector.
        /// </summary>
        /// <param name="relativeTargetPosition">Target point relative to the CPU vessel in SHIP-RAW axes.</param>
        public void LookAt(Vector3 relativeTargetPosition)
        {
            var cameraWorldPosition = ResolveDesiredWorldPosition();
            var targetWorldPosition = GetCpuOrigin() + relativeTargetPosition;
            var facingVector = targetWorldPosition - cameraWorldPosition;

            if (facingVector.sqrMagnitude < 0.000001f)
            {
                throw new kOS.Safe.Exceptions.KOSException("FREECAM:LOOKAT target must not be the current camera position.");
            }

            var worldRotation = BuildFacingRotationFromVector(facingVector, cameraWorldPosition);
            hasManualRotation = false;
            StoreWorldRotationForCurrentAnchor(worldRotation);
            SetEulerFieldsFromRotation(worldRotation);
            ApplyDesiredPoseToParent();
            status = "Free camera look-at set.";
        }

        /// <summary>
        /// MOVE is the controller-side implementation of
        /// `set cam:position to cam:position + delta`, preserving the active anchor
        /// semantics by routing the result through the normal position storage path.
        /// </summary>
        /// <param name="delta">SHIP-RAW position delta to add to the current camera position.</param>
        public void Move(Vector3 delta)
        {
            var relativePosition = GetCurrentRelativePosition() + delta;
            var worldPosition = GetCpuOrigin() + relativePosition;
            StoreWorldPositionForCurrentAnchor(worldPosition);
            ApplyDesiredPoseToParent();
            status = "Free camera moved.";
        }

        internal FreeCameraAnchor AnchorMode
        {
            get
            {
                EnsurePartAnchorIsStillUsable();
                return anchor;
            }
        }

        public Vessel AnchorVessel
        {
            get { return GetAnchorVessel(); }
        }

        public Part AnchorPart
        {
            get { return GetAnchorPart(); }
        }

        public CelestialBody AnchorBody
        {
            get { return GetAnchorBody(); }
        }

        /// <summary>
        /// Sets the anchor from a string shorthand.  String anchors remain as
        /// conveniences for scripts; object anchors are preferred for explicit vessel,
        /// part, or body targets.
        /// </summary>
        /// <param name="value">Anchor shorthand such as SHIP/VESSEL or BODY.</param>
        public void SetAnchor(string value)
        {
            var parsed = ParseAnchor(value);
            if (parsed == FreeCameraAnchor.Body)
            {
                SetAnchor(GetCurrentAnchorBody());
                return;
            }

            SetAnchor(GetAnchorVessel());
        }

        /// <summary>
        /// Anchors the camera to a vessel CoM.  The current visual pose is preserved
        /// while the vessel-relative raw/facing anchor representations are rebuilt.
        /// </summary>
        /// <param name="vessel">Vessel to follow as the anchor.</param>
        public void SetAnchor(Vessel vessel)
        {
            if (vessel == null)
            {
                throw new System.ArgumentException("FREECAM:ANCHOR vessel is unavailable.");
            }

            ChangeAnchor(FreeCameraAnchor.Ship, vessel, null, null, "vessel: " + vessel.vesselName);
        }

        /// <summary>
        /// Anchors the camera to a part transform.  Position and facing are stored in
        /// part-local space, so CoM shifts from fuel burn, staging, or cargo changes do
        /// not move the camera relative to the selected part.
        /// </summary>
        /// <param name="part">Part whose transform should carry the camera pose.</param>
        public void SetAnchor(Part part)
        {
            if (part == null || part.transform == null)
            {
                throw new System.ArgumentException("FREECAM:ANCHOR part is unavailable.");
            }

            var name = string.IsNullOrEmpty(part.partInfo != null ? part.partInfo.title : null) ? part.partName : part.partInfo.title;
            ChangeAnchor(FreeCameraAnchor.Part, null, part, null, "part: " + name);
        }

        /// <summary>
        /// Anchors the camera to a celestial body.  The camera position is stored in
        /// body-local transform space so it survives KSP floating-origin movement.
        /// </summary>
        /// <param name="body">Celestial body to use as the body-fixed anchor.</param>
        public void SetAnchor(CelestialBody body)
        {
            if (body == null || body.transform == null)
            {
                throw new System.ArgumentException("FREECAM:ANCHOR body is unavailable.");
            }

            ChangeAnchor(FreeCameraAnchor.Body, null, null, body, "body: " + body.bodyName);
        }

        /// <summary>
        /// Changes anchor type/target while preserving the current world-space pose.
        /// </summary>
        /// <param name="newAnchor">Anchor representation to use after the change.</param>
        /// <param name="vessel">Vessel target when using a vessel anchor.</param>
        /// <param name="part">Part target when using a part anchor.</param>
        /// <param name="body">Body target when using a body anchor.</param>
        /// <param name="messageTarget">Human-readable target text for status/HUD output.</param>
        private void ChangeAnchor(FreeCameraAnchor newAnchor, Vessel vessel, Part part, CelestialBody body, string messageTarget)
        {
            var currentWorldPosition = ResolveDesiredWorldPosition();
            var currentWorldRotation = ResolveDesiredWorldRotation();

            anchor = newAnchor;
            if (vessel != null)
            {
                anchorVessel = vessel;
            }
            if (part != null)
            {
                anchorPart = part;
            }
            if (body != null)
            {
                bodyAnchorBody = body;
            }

            StoreWorldPositionForCurrentAnchor(currentWorldPosition);
            StoreWorldRotationForCurrentAnchor(currentWorldRotation);
            ApplyDesiredPoseToParent();
            status = "Free camera anchor set to " + messageTarget + ".";
            ShowHudMessage("Anchor: " + messageTarget);
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

        /// <summary>
        /// The controller is a singleton, while kOS CPUs are not.  Track the most
        /// recent SharedObjects provider so SHIP-RAW conversion uses the CPU vessel
        /// that is currently driving the suffix API.
        /// </summary>
        /// <param name="sharedObjects">Current kOS CPU context used for SHIP-RAW conversion.</param>
        public void SetSharedObjects(SharedObjects sharedObjects)
        {
            shared = sharedObjects;
            if (anchorVessel == null && shared != null && shared.Vessel != null)
            {
                anchorVessel = shared.Vessel;
            }
        }

        /// <summary>
        /// Records the user's desired freecam state.  Activation may be deferred if
        /// KSP is temporarily in map view or another non-flight camera mode.
        /// </summary>
        /// <param name="enabled">Whether the user wants freecam ownership enabled.</param>
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

        /// <summary>
        /// Captures the current stock FlightCamera pose/FOV into the freecam desired
        /// state without necessarily enabling freecam.  Useful as a safe baseline for
        /// later scripted adjustments.
        /// </summary>
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

        /// <summary>
        /// Reset prefers the original stock-camera snapshot captured on activation;
        /// if there is no valid snapshot, clear all stored freecam pose state instead.
        /// </summary>
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

        /// <summary>
        /// LateUpdate is the ownership loop.  It runs after normal vessel/camera
        /// updates, keeps FlightCamera parented under our transform while active, and
        /// releases or suspends control when KSP changes camera context.
        /// </summary>
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

        /// <summary>
        /// Attempts to activate immediately when FlightCamera is available and in
        /// Flight mode.  Non-IVA camera modes are treated as temporary suspension; IVA
        /// is treated as a user context switch and disables the request.
        /// </summary>
        /// <returns>True when freecam is active after the attempt.</returns>
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

        /// <summary>
        /// Takes ownership of KSP's existing FlightCamera by parenting it under a
        /// controlled transform.  The original stock state is captured once so disable
        /// can restore normal KSP camera behavior.
        /// </summary>
        /// <param name="captureOriginalCamera">True to replace the saved stock-camera snapshot before activation.</param>
        /// <returns>True when FlightCamera ownership was acquired.</returns>
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
                requestedFov = Mathf.Clamp(originalCamera.Fov, 0.0001f, 179f);
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

            var fov = Mathf.Clamp(requestedFov, 0.001f, 179f);
            flightCamera.FieldOfView = fov;
            flightCamera.SetFoV(fov);
        }

        /// <summary>
        /// Reasserts the freecam pose and disables stock FlightCamera updates every
        /// frame.  This protects against KSP or another system partially restoring
        /// target/local transform state while freecam is active.
        /// </summary>
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

        /// <summary>
        /// Applies the resolved desired pose to the camera parent, not directly to
        /// FlightCamera.  FlightCamera itself remains zeroed locally under this parent.
        /// </summary>
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

        /// <summary>
        /// Re-applies BODY-anchor pose immediately before the active flight camera renders.
        /// KSP can apply floating-origin/Krakensbane corrections after LateUpdate, so a
        /// body-fixed camera resolved only in LateUpdate may jitter around altitude
        /// transitions. This targeted render-time correction is limited to BODY anchors
        /// in Flight camera mode to avoid reintroducing the older IVA/pre-cull issues.
        /// </summary>
        /// <param name="renderingCamera">Camera currently entering Unity's pre-cull phase.</param>
        private void ApplyBodyAnchorRenderCorrection(Camera renderingCamera)
        {
            if (!ShouldApplyBodyAnchorRenderCorrection(renderingCamera))
            {
                return;
            }

            var worldPosition = ResolveDesiredWorldPosition();
            var worldRotation = ResolveDesiredWorldRotation();
            cameraParent.transform.SetPositionAndRotation(worldPosition, worldRotation);

            if (flightCamera != null)
            {
                flightCamera.transform.localPosition = Vector3.zero;
                flightCamera.transform.localRotation = Quaternion.identity;
            }

            desiredPosition = worldPosition;
            desiredRotation = worldRotation;
            hasDesiredPose = true;
        }

        /// <summary>
        /// Guards the BODY-anchor pre-cull correction so only the owned FlightCamera is
        /// touched, and only while freecam is active in normal Flight camera mode.
        /// </summary>
        /// <param name="renderingCamera">Camera Unity is about to render.</param>
        /// <returns>True when it is safe and useful to run the BODY-anchor correction.</returns>
        private bool ShouldApplyBodyAnchorRenderCorrection(Camera renderingCamera)
        {
            if (!active || anchor != FreeCameraAnchor.Body || cameraParent == null || flightCamera == null)
            {
                return false;
            }

            if (!requestedEnabled || !IsFlightCameraAvailable || !IsCurrentCameraMode(CameraManager.CameraMode.Flight))
            {
                return false;
            }

            if (bodyAnchorBody == null || bodyAnchorBody.transform == null)
            {
                return false;
            }

            return renderingCamera != null && flightCamera.mainCamera != null && renderingCamera == flightCamera.mainCamera;
        }

        /// <summary>
        /// Converts the stored anchor representation back into a Unity world position.
        /// SHIP/FACING rotates the stored offset with the vessel, PART resolves from
        /// part-local coordinates, and BODY resolves from body-local coordinates to
        /// survive KSP floating-origin shifts.
        /// </summary>
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

                if (anchor == FreeCameraAnchor.Part)
                {
                    var resolvedPartPosition = ResolvePartAnchoredWorldPosition();
                    if (resolvedPartPosition.HasValue)
                    {
                        return resolvedPartPosition.Value;
                    }

                    SwitchLostPartAnchorToVessel();
                    return ResolveDesiredWorldPosition();
                }

                if (anchor == FreeCameraAnchor.Body)
                {
                    var resolvedBodyPosition = ResolveBodyAnchoredWorldPosition();
                    if (resolvedBodyPosition.HasValue)
                    {
                        return resolvedBodyPosition.Value;
                    }

                    return bodyFallbackWorldPosition;
                }
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

        /// <summary>
        /// Resolves the desired camera rotation.  Vessel-facing anchor frames store
        /// rotation relative to the anchor vessel, while HPR mode rebuilds a rotation
        /// from local-horizon heading/pitch/roll fields.
        /// </summary>
        private Quaternion ResolveDesiredWorldRotation()
        {
            if (anchor == FreeCameraAnchor.Part && hasDesiredPose)
            {
                var resolvedPartRotation = ResolvePartAnchoredWorldRotation();
                if (resolvedPartRotation.HasValue)
                {
                    return resolvedPartRotation.Value;
                }

                SwitchLostPartAnchorToVessel();
                return ResolveDesiredWorldRotation();
            }

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

        /// <summary>
        /// Public POSITION is always SHIP-RAW, even when the internal anchor is BODY
        /// or vessel-facing.  This converts the currently resolved world pose back to
        /// the CPU vessel's raw-axis offset.
        /// </summary>
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

        /// <summary>
        /// Stores a world position in every anchor representation we may need later.
        /// Keeping all representations warm lets ANCHOR/ANCHORFRAME changes preserve
        /// the current visual pose instead of teleporting the camera.
        /// </summary>
        private void StoreWorldPositionForCurrentAnchor(Vector3 worldPosition)
        {
            hasPositionState = true;

            // Keep all available anchor representations current.  This lets changing
            // ANCHOR preserve the current visual position without teleporting.
            StoreBodyAnchoredWorldPosition(worldPosition);
            StorePartAnchoredWorldPosition(worldPosition);

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

        /// <summary>
        /// Stores both the absolute desired rotation and the vessel-relative rotation
        /// used by ANCHORFRAME=FACING.
        /// </summary>
        private void StoreWorldRotationForCurrentAnchor(Quaternion worldRotation)
        {
            desiredRotation = worldRotation;
            hasDesiredPose = true;

            StorePartAnchoredWorldRotation(worldRotation);

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

        /// <summary>
        /// BODY anchoring stores positions in the celestial body's transform space,
        /// not raw Unity world space, because KSP's floating origin can shift world
        /// coordinates during flight.
        /// </summary>
        private void StoreBodyAnchoredWorldPosition(Vector3 worldPosition)
        {
            bodyFallbackWorldPosition = worldPosition;

            var body = anchor == FreeCameraAnchor.Body && bodyAnchorBody != null ? bodyAnchorBody : GetCurrentAnchorBody();
            if (body != null && body.transform != null)
            {
                bodyAnchorBody = body;
                bodyLocalPosition = body.transform.InverseTransformPoint(worldPosition);
            }
        }

        /// <summary>
        /// PART anchoring stores the camera position in the selected part's local
        /// transform space so the camera stays fixed relative to that part instead of
        /// following vessel CoM shifts.
        /// </summary>
        private void StorePartAnchoredWorldPosition(Vector3 worldPosition)
        {
            partFallbackWorldPosition = worldPosition;

            var part = GetAnchorPart();
            if (part != null && part.transform != null)
            {
                partLocalPosition = part.transform.InverseTransformPoint(worldPosition);
            }
        }

        /// <summary>
        /// PART anchoring stores camera facing relative to the selected part transform,
        /// giving root-part/part-mounted cameras their expected attached behavior.
        /// </summary>
        private void StorePartAnchoredWorldRotation(Quaternion worldRotation)
        {
            partFallbackWorldRotation = worldRotation;

            var part = GetAnchorPart();
            if (part != null && part.transform != null)
            {
                partLocalRotation = Quaternion.Inverse(part.transform.rotation) * worldRotation;
            }
        }

        /// <summary>
        /// Resolves the body-local anchor position back to world space.  Returning null
        /// allows callers to fall back if the body/transform is no longer available.
        /// </summary>
        /// <returns>The resolved world position, or null when BODY state cannot be resolved.</returns>
        private Vector3? ResolveBodyAnchoredWorldPosition()
        {
            if (bodyAnchorBody != null && bodyAnchorBody.transform != null)
            {
                return bodyAnchorBody.transform.TransformPoint(bodyLocalPosition);
            }

            return null;
        }

        /// <summary>
        /// Resolves the part-local anchor position back to world space.  Returning null
        /// lets callers fall back if the part was destroyed or unloaded.
        /// </summary>
        /// <returns>The resolved world position, or null when PART state cannot be resolved.</returns>
        private Vector3? ResolvePartAnchoredWorldPosition()
        {
            var part = GetAnchorPart();
            if (part != null && part.transform != null)
            {
                return part.transform.TransformPoint(partLocalPosition);
            }

            return null;
        }

        /// <summary>
        /// Resolves the part-local anchor rotation back to world space.  Returning null
        /// lets callers fall back if the part was destroyed or unloaded.
        /// </summary>
        /// <returns>The resolved world rotation, or null when PART state cannot be resolved.</returns>
        private Quaternion? ResolvePartAnchoredWorldRotation()
        {
            var part = GetAnchorPart();
            if (part != null && part.transform != null)
            {
                return part.transform.rotation * partLocalRotation;
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
            if (anchor == FreeCameraAnchor.Body && bodyAnchorBody != null)
            {
                return bodyAnchorBody;
            }

            return GetCurrentAnchorBody();
        }

        private CelestialBody GetCurrentAnchorBody()
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

        private Part GetAnchorPart()
        {
            if (IsUsablePart(anchorPart))
            {
                return anchorPart;
            }

            return null;
        }

        /// <summary>
        /// Converts a lost PART anchor into a vessel anchor while preserving the last
        /// resolved camera pose.  If the part was decoupled or undocked and remains
        /// loaded, the part is still usable and this method is not reached; if the
        /// part was destroyed or unloaded, the camera falls back to the part's current
        /// vessel when available, otherwise the CPU vessel.
        /// </summary>
        private void EnsurePartAnchorIsStillUsable()
        {
            if (anchor == FreeCameraAnchor.Part && !IsUsablePart(anchorPart))
            {
                SwitchLostPartAnchorToVessel();
            }
        }

        /// <summary>
        /// Degrades an unavailable part anchor to a vessel anchor instead of leaving
        /// the camera fixed at a stale raw Unity world pose.
        /// </summary>
        private void SwitchLostPartAnchorToVessel()
        {
            if (anchor != FreeCameraAnchor.Part)
            {
                return;
            }

            var lostPart = anchorPart;
            var fallbackPosition = ResolveFallbackWorldPosition();
            var fallbackRotation = ResolveFallbackWorldRotation();
            var fallbackVessel = GetFallbackVesselForLostPart(lostPart);

            anchor = FreeCameraAnchor.Ship;
            anchorPart = null;

            if (fallbackVessel != null)
            {
                anchorVessel = fallbackVessel;
            }

            StoreWorldPositionForCurrentAnchor(fallbackPosition);
            StoreWorldRotationForCurrentAnchor(fallbackRotation);
            ApplyDesiredPoseToParent();

            status = fallbackVessel != null
                ? "Free camera part anchor unavailable; switched to vessel anchor."
                : "Free camera part anchor unavailable; switched to CPU vessel anchor.";
            ShowHudMessage("Part anchor unavailable; using vessel anchor");
        }

        /// <summary>
        /// Prefers the lost part's current vessel, which covers unloaded remote vessels,
        /// then falls back to the CPU vessel used for SHIP-RAW coordinates.
        /// </summary>
        /// <param name="part">The part that was previously used as the anchor.</param>
        /// <returns>A vessel suitable for a degraded ship anchor, or null.</returns>
        private Vessel GetFallbackVesselForLostPart(Part part)
        {
            try
            {
                if (part != null && IsUsableVessel(part.vessel))
                {
                    return part.vessel;
                }
            }
            catch (System.Exception)
            {
                // Unity/KSP can leave destroyed objects in a state where property
                // access throws.  Fall through to the CPU-vessel fallback.
            }

            var cpuVessel = GetCpuVessel();
            return IsUsableVessel(cpuVessel) ? cpuVessel : null;
        }

        /// <summary>
        /// Finds a last-known world position without using the current anchor resolver,
        /// avoiding recursion while recovering from a lost part anchor.
        /// </summary>
        /// <returns>The best available last-known camera world position.</returns>
        private Vector3 ResolveFallbackWorldPosition()
        {
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

            return GetCpuOrigin();
        }

        /// <summary>
        /// Finds a last-known world rotation without using the current anchor resolver,
        /// avoiding recursion while recovering from a lost part anchor.
        /// </summary>
        /// <returns>The best available last-known camera world rotation.</returns>
        private Quaternion ResolveFallbackWorldRotation()
        {
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

        /// <summary>
        /// Anchor vessel is lazily repaired if the stored vessel disappeared or is no
        /// longer usable, falling back to the current CPU vessel when possible.
        /// </summary>
        /// <returns>A usable anchor vessel, or null if none is available.</returns>
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

        private static bool IsUsablePart(Part part)
        {
            return part != null &&
                   part.transform != null &&
                   part.vessel != null &&
                   part.vessel.loaded;
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

        /// <summary>
        /// Builds a camera rotation from a look direction using the camera's body-up
        /// vector.  Several fallbacks keep Quaternion.LookRotation stable near poles
        /// or when forward is parallel to the preferred up vector.
        /// </summary>
        /// <param name="facingVector">World-space look direction, not a target point.</param>
        /// <param name="cameraWorldPosition">Camera world position used to derive body-up.</param>
        private Quaternion BuildFacingRotationFromVector(Vector3 facingVector, Vector3 cameraWorldPosition)
        {
            if (facingVector.sqrMagnitude < 0.000001f)
            {
                throw new kOS.Safe.Exceptions.KOSException("FREECAM:FACING vector must be non-zero.");
            }

            var forward = facingVector.normalized;

            // Match kOS lookdirup(facingVector, cam:position - body:position):
            // a vector FACING assignment is a look direction, and the effective up
            // direction is the camera's local body-up vector at the camera position.
            Vector3 up;
            Vector3 surfaceNorth;
            Vector3 surfaceEast;
            GetStableReferenceFrame(cameraWorldPosition, out up, out surfaceNorth, out surfaceEast);

            if (up.sqrMagnitude < 0.000001f || Vector3.Cross(forward, up).sqrMagnitude < 0.000001f)
            {
                up = surfaceNorth;
            }

            if (up.sqrMagnitude < 0.000001f || Vector3.Cross(forward, up).sqrMagnitude < 0.000001f)
            {
                up = surfaceEast;
            }

            if (up.sqrMagnitude < 0.000001f || Vector3.Cross(forward, up).sqrMagnitude < 0.000001f)
            {
                up = Vector3.up;
            }

            if (Vector3.Cross(forward, up).sqrMagnitude < 0.000001f)
            {
                up = Vector3.right;
            }

            return Quaternion.LookRotation(forward, up.normalized);
        }

        /// <summary>
        /// Converts the public local-horizon HPR fields into a world rotation.  This
        /// is deliberately based on the CPU vessel/body frame rather than the anchor
        /// vessel frame, matching the documented current limitation.
        /// </summary>
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

        /// <summary>
        /// Decomposes an exact Direction/FACING quaternion into approximate HPR fields
        /// for display and for continuing from the same pose if the user later edits
        /// HEADING, PITCH, or ROLL.
        /// </summary>
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

        /// <summary>
        /// Builds a numerically stable local-horizon frame at the supplied world
        /// origin.  Used by HPR and vector-facing up semantics; includes fallbacks for
        /// poles and degenerate body-axis projections.
        /// </summary>
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

        /// <summary>
        /// Computes a signed angle around an explicit axis without relying on newer
        /// Unity APIs.
        /// </summary>
        /// <returns>The signed angle in degrees.</returns>
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
                    return FreeCameraAnchor.Ship;
                case "BODY":
                case "WORLD":
                case "FIXED":
                    return FreeCameraAnchor.Body;
                default:
                    throw new System.ArgumentException("FREECAM:ANCHOR string must be SHIP/VESSEL or BODY.");
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

        /// <summary>
        /// Map view and similar camera modes are temporary.  Save the current freecam
        /// pose, restore stock camera control, and remember that we should resume when
        /// KSP returns to Flight camera mode.
        /// </summary>
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

        /// <summary>
        /// IVA/internal camera mode is a user-selected context, not a temporary map
        /// transition.  Stop freecam immediately and delay FlightCamera reparenting
        /// until KSP is safely back in Flight mode to avoid corrupting IVA entry.
        /// </summary>
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

        /// <summary>
        /// Persists the currently rendered freecam pose before handing FlightCamera
        /// back to KSP, so a later resume starts from the same visual camera pose.
        /// </summary>
        private void SaveCurrentFreeCameraPose()
        {
            if (cameraParent == null)
            {
                return;
            }

            StoreWorldPositionForCurrentAnchor(cameraParent.transform.position);
            StoreWorldRotationForCurrentAnchor(cameraParent.transform.rotation);
        }

        /// <summary>
        /// Completes the stock-camera restore that was intentionally skipped during
        /// IVA entry.  It only runs after KSP reports that Flight camera mode is active
        /// again.
        /// </summary>
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

        /// <summary>
        /// Restores the captured stock FlightCamera transform, mode, target, distance,
        /// FOV, and near clip.  This method assumes originalCamera is still the
        /// snapshot captured before freecam took ownership.
        /// </summary>
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

        /// <summary>
        /// Common restore path for disable, scene changes, suspension, and error
        /// recovery.  clearRequest distinguishes a final user/error disable from a
        /// temporary suspension that may resume later.
        /// </summary>
        /// <param name="clearRequest">True when this restore should clear the user's enable request.</param>
        /// <param name="reason">Status message describing why restore happened.</param>
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

        /// <summary>
        /// FlightCamera.SetTargetNone is used during activation; restoring a vessel
        /// target is required so stock camera follow behavior works again after disable.
        /// </summary>
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

        /// <summary>
        /// Detects whether KSP reset FlightCamera's parent or another mod took it.  A
        /// known stock-parent reset can be repaired; an unknown parent change disables
        /// freecam to avoid leaving the user's camera broken.
        /// </summary>
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

        /// <summary>
        /// Scene transitions invalidate camera/vessel/body references.  Clear all
        /// cached state rather than carrying transforms or snapshots into the next
        /// flight scene.
        /// </summary>
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
            anchorPart = null;
            anchorOffset = Vector3.zero;
            anchorFacingOffset = Vector3.zero;
            anchorFacingRotation = Quaternion.identity;
            partLocalPosition = Vector3.zero;
            partLocalRotation = Quaternion.identity;
            partFallbackWorldPosition = Vector3.zero;
            partFallbackWorldRotation = Quaternion.identity;

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

        /// <summary>
        /// Unity render callback used only to run the guarded BODY-anchor correction.
        /// </summary>
        /// <param name="renderingCamera">Camera entering pre-cull.</param>
        private void OnCameraPreCull(Camera renderingCamera)
        {
            ApplyBodyAnchorRenderCorrection(renderingCamera);
        }

        /// <summary>
        /// CameraManager events let us release control immediately when the player
        /// changes camera context, instead of waiting for the next normal update path
        /// to notice the mode change.
        /// </summary>
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

        /// <summary>
        /// A scene load can destroy FlightCamera and vessel transforms.  Restore first
        /// while objects may still exist, then clear all stored references.
        /// </summary>
        private void OnGameSceneLoadRequested(GameScenes scene)
        {
            RestoreCamera(true, "Scene change requested; stock camera restored.");
            ClearFreeCameraStateForNewFlight("Scene change requested; free camera pose state cleared.");
        }

        /// <summary>
        /// Register once because the controller persists across scenes.
        /// </summary>
        private void RegisterEvents()
        {
            if (!eventsRegistered)
            {
                GameEvents.OnCameraChange.Add(OnCameraChange);
                GameEvents.onGameSceneLoadRequested.Add(OnGameSceneLoadRequested);
                eventsRegistered = true;
            }

            if (!cameraRenderEventRegistered)
            {
                Camera.onPreCull += OnCameraPreCull;
                cameraRenderEventRegistered = true;
            }
        }

        /// <summary>
        /// Unregister on destroy to avoid stale delegates pointing at a dead singleton.
        /// </summary>
        private void UnregisterEvents()
        {
            if (eventsRegistered)
            {
                GameEvents.OnCameraChange.Remove(OnCameraChange);
                GameEvents.onGameSceneLoadRequested.Remove(OnGameSceneLoadRequested);
                eventsRegistered = false;
            }

            if (cameraRenderEventRegistered)
            {
                Camera.onPreCull -= OnCameraPreCull;
                cameraRenderEventRegistered = false;
            }
        }
    }
}
