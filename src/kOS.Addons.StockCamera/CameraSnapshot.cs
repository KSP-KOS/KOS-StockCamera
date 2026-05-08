using UnityEngine;

namespace kOS.AddOns.StockCamera
{
    internal struct CameraSnapshot
    {
        public bool IsValid;
        public Transform Parent;
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 LocalPosition;
        public Quaternion LocalRotation;
        public float NearClip;
        public float Distance;
        public FlightCamera.Modes Mode;
        public float Fov;
        public Vessel VesselTarget;

        public static CameraSnapshot Capture(FlightCamera camera)
        {
            var snapshot = new CameraSnapshot();
            if (camera == null)
            {
                return snapshot;
            }

            snapshot.IsValid = true;
            snapshot.Parent = camera.transform.parent;
            snapshot.Position = camera.transform.position;
            snapshot.Rotation = camera.transform.rotation;
            snapshot.LocalPosition = camera.transform.localPosition;
            snapshot.LocalRotation = camera.transform.localRotation;
            snapshot.NearClip = camera.mainCamera != null ? camera.mainCamera.nearClipPlane : 0.01f;
            snapshot.Distance = camera.Distance;
            snapshot.Mode = camera.mode;
            snapshot.Fov = camera.FieldOfView;
            snapshot.VesselTarget = camera.vesselTarget;
            return snapshot;
        }
    }
}
