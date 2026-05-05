using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Exceptions;
using kOS.Safe.Utilities;
using kOS.Suffixed;

namespace kOS.AddOns.StockCamera
{
    [KOSNomenclature("FreeCamera")]
    public class FreeCameraValue : Structure
    {
        private readonly SharedObjects shared;
        private readonly FreeCameraController controller;

        public FreeCameraValue(SharedObjects shared)
        {
            this.shared = shared;
            controller = FreeCameraController.Instance;
            controller.SetSharedObjects(shared);

            AddSuffix("ENABLED", new SetSuffix<BooleanValue>(GetEnabled, SetEnabled));
            AddSuffix("ACTIVE", new Suffix<BooleanValue>(GetActive));
            AddSuffix("FOV", new SetSuffix<ScalarValue>(GetFov, SetFov));
            AddSuffix("STATUS", new Suffix<StringValue>(GetStatus));
            AddSuffix("AVAILABLE", new Suffix<BooleanValue>(GetAvailable));

            AddSuffix("POSITION", new SetSuffix<Vector>(GetPosition, SetPosition));
            AddSuffix("ORIENTATION", new SetSuffix<Direction>(GetOrientation, SetOrientation));
            AddSuffix("ANCHOR", new SetSuffix<StringValue>(GetAnchor, SetAnchor));
            AddSuffix("ANCHORFRAME", new SetSuffix<StringValue>(GetAnchorFrame, SetAnchorFrame));
            AddSuffix("ANCHORVESSEL", new SetSuffix<VesselTarget>(GetAnchorVessel, SetAnchorVessel));
            AddSuffix("HEADING", new SetSuffix<ScalarValue>(GetHeading, SetHeading));
            AddSuffix("HDG", new SetSuffix<ScalarValue>(GetHeading, SetHeading));
            AddSuffix("PITCH", new SetSuffix<ScalarValue>(GetPitch, SetPitch));
            AddSuffix("ROLL", new SetSuffix<ScalarValue>(GetRoll, SetRoll));

            AddSuffix("SETPOSE", new TwoArgsSuffix<Vector, Direction>(SetPose));
            AddSuffix("RESET", new NoArgsVoidSuffix(Reset));
            AddSuffix("COPYFROMSTOCK", new NoArgsVoidSuffix(CopyFromStock));
        }

        private StringValue GetStatus()
        {
            return new StringValue(controller.Status);
        }

        private BooleanValue GetAvailable()
        {
            return FreeCameraController.IsFlightCameraAvailable ? BooleanValue.True : BooleanValue.False;
        }

        private BooleanValue GetEnabled()
        {
            return controller.RequestedEnabled ? BooleanValue.True : BooleanValue.False;
        }

        private void SetEnabled(BooleanValue value)
        {
            EnsureFlightScene("ENABLED");
            controller.SetSharedObjects(shared);
            controller.SetRequestedEnabled(value);
        }

        private BooleanValue GetActive()
        {
            return controller.Active ? BooleanValue.True : BooleanValue.False;
        }

        private ScalarValue GetFov()
        {
            return ScalarValue.Create(controller.Fov);
        }

        private void SetFov(ScalarValue value)
        {
            var fov = (float)value.GetDoubleValue();
            if (fov <= 0f || fov >= 180f)
            {
                throw new KOSException("FREECAM:FOV must be greater than 0 and less than 180 degrees.");
            }

            controller.Fov = fov;
        }

        private Vector GetPosition()
        {
            return new Vector(controller.RelativePosition);
        }

        private void SetPosition(Vector value)
        {
            EnsureFlightScene("POSITION");
            controller.SetSharedObjects(shared);
            controller.RelativePosition = value.ToVector3();
        }

        private Direction GetOrientation()
        {
            return new Direction(controller.Orientation);
        }

        private void SetOrientation(Direction value)
        {
            EnsureFlightScene("ORIENTATION");
            if (value == null)
            {
                throw new KOSException("FREECAM:ORIENTATION must be a Direction.");
            }

            controller.SetSharedObjects(shared);
            controller.Orientation = value.Rotation;
        }

        private void SetPose(Vector position, Direction orientation)
        {
            EnsureFlightScene("SETPOSE");
            if (position == null)
            {
                throw new KOSException("FREECAM:SETPOSE first argument must be a Vector position.");
            }

            if (orientation == null)
            {
                throw new KOSException("FREECAM:SETPOSE second argument must be a Direction orientation.");
            }

            controller.SetSharedObjects(shared);
            controller.SetPose(position.ToVector3(), orientation.Rotation);
        }

        private StringValue GetAnchor()
        {
            return new StringValue(controller.Anchor);
        }

        private void SetAnchor(StringValue value)
        {
            EnsureFlightScene("ANCHOR");
            controller.SetSharedObjects(shared);
            try
            {
                controller.Anchor = value.ToString();
            }
            catch (System.ArgumentException ex)
            {
                throw new KOSException(ex.Message);
            }
        }

        private StringValue GetAnchorFrame()
        {
            return new StringValue(controller.AnchorFrame);
        }

        private void SetAnchorFrame(StringValue value)
        {
            EnsureFlightScene("ANCHORFRAME");
            controller.SetSharedObjects(shared);
            try
            {
                controller.AnchorFrame = value.ToString();
            }
            catch (System.ArgumentException ex)
            {
                throw new KOSException(ex.Message);
            }
        }

        private VesselTarget GetAnchorVessel()
        {
            var vessel = controller.AnchorVessel;
            if (vessel == null)
            {
                throw new KOSException("FREECAM:ANCHORVESSEL is unavailable because no vessel is available.");
            }

            return VesselTarget.CreateOrGetExisting(vessel, shared);
        }

        private void SetAnchorVessel(VesselTarget value)
        {
            EnsureFlightScene("ANCHORVESSEL");
            if (value == null || value.Vessel == null)
            {
                throw new KOSException("FREECAM:ANCHORVESSEL must be a Vessel.");
            }

            controller.SetSharedObjects(shared);
            controller.AnchorVessel = value.Vessel;
        }

        private ScalarValue GetHeading()
        {
            return ScalarValue.Create(controller.Heading);
        }

        private void SetHeading(ScalarValue value)
        {
            EnsureFlightScene("HEADING");
            controller.Heading = (float)value.GetDoubleValue();
        }

        private ScalarValue GetPitch()
        {
            return ScalarValue.Create(controller.Pitch);
        }

        private void SetPitch(ScalarValue value)
        {
            EnsureFlightScene("PITCH");
            controller.Pitch = (float)value.GetDoubleValue();
        }

        private ScalarValue GetRoll()
        {
            return ScalarValue.Create(controller.Roll);
        }

        private void SetRoll(ScalarValue value)
        {
            EnsureFlightScene("ROLL");
            controller.Roll = (float)value.GetDoubleValue();
        }

        private void Reset()
        {
            EnsureFlightScene("RESET");
            controller.SetSharedObjects(shared);
            controller.ResetCamera();
        }

        private void CopyFromStock()
        {
            EnsureFlightScene("COPYFROMSTOCK");
            controller.SetSharedObjects(shared);
            controller.CopyFromStockCamera();
        }

        private static void EnsureFlightScene(string suffixName)
        {
            if (!FreeCameraController.IsFlightCameraAvailable)
            {
                throw new KOSException("FREECAM:" + suffixName + " is only available in the flight scene after FlightCamera exists.");
            }
        }
    }
}
