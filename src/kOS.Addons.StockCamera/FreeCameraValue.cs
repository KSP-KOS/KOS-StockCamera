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
            AddSuffix("DISTANCE", new SetSuffix<ScalarValue>(GetDistance, SetDistance));
            AddSuffix("FACING", new SetSuffix<Structure>(GetFacing, SetFacing));
            AddSuffix("ANCHOR", new SetSuffix<Structure>(GetAnchor, SetAnchor));
            AddSuffix("ANCHORFRAME", new SetSuffix<StringValue>(GetAnchorFrame, SetAnchorFrame));
            AddSuffix("HEADING", new SetSuffix<ScalarValue>(GetHeading, SetHeading));
            AddSuffix("HDG", new SetSuffix<ScalarValue>(GetHeading, SetHeading));
            AddSuffix("PITCH", new SetSuffix<ScalarValue>(GetPitch, SetPitch));
            AddSuffix("ROLL", new SetSuffix<ScalarValue>(GetRoll, SetRoll));

            AddSuffix("SETPOSE", new TwoArgsSuffix<Vector, Structure>(SetPose));
            AddSuffix("LOOKAT", new OneArgsSuffix<Vector>(LookAt));
            AddSuffix("MOVE", new OneArgsSuffix<Vector>(Move));
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

        private ScalarValue GetDistance()
        {
            return ScalarValue.Create(controller.Distance);
        }

        private void SetDistance(ScalarValue value)
        {
            EnsureFlightScene("DISTANCE");
            var distance = (float)value.GetDoubleValue();
            if (distance < 0f)
            {
                throw new KOSException("FREECAM:DISTANCE must be greater than or equal to 0.");
            }

            controller.SetSharedObjects(shared);
            controller.Distance = distance;
        }

        private Structure GetFacing()
        {
            return new Direction(controller.Facing);
        }

        private void SetFacing(Structure value)
        {
            EnsureFlightScene("FACING");
            if (value == null)
            {
                throw new KOSException("FREECAM:FACING must be a Direction or non-zero Vector direction.");
            }

            controller.SetSharedObjects(shared);

            var direction = value as Direction;
            if (direction != null)
            {
                controller.Facing = direction.Rotation;
                return;
            }

            var vector = value as Vector;
            if (vector != null)
            {
                controller.SetFacingVector(vector.ToVector3());
                return;
            }

            throw new KOSException("FREECAM:FACING must be a Direction or non-zero Vector direction.");
        }

        private void SetPose(Vector position, Structure facing)
        {
            EnsureFlightScene("SETPOSE");
            if (position == null)
            {
                throw new KOSException("FREECAM:SETPOSE first argument must be a Vector position.");
            }

            if (facing == null)
            {
                throw new KOSException("FREECAM:SETPOSE second argument must be a Direction or non-zero Vector direction.");
            }

            controller.SetSharedObjects(shared);

            var direction = facing as Direction;
            if (direction != null)
            {
                controller.SetPose(position.ToVector3(), direction.Rotation);
                return;
            }

            var vector = facing as Vector;
            if (vector != null)
            {
                controller.SetPose(position.ToVector3(), vector.ToVector3());
                return;
            }

            throw new KOSException("FREECAM:SETPOSE second argument must be a Direction or non-zero Vector direction.");
        }

        private void LookAt(Vector targetPosition)
        {
            EnsureFlightScene("LOOKAT");
            if (targetPosition == null)
            {
                throw new KOSException("FREECAM:LOOKAT argument must be a Vector target position.");
            }

            controller.SetSharedObjects(shared);
            controller.LookAt(targetPosition.ToVector3());
        }

        private void Move(Vector delta)
        {
            EnsureFlightScene("MOVE");
            if (delta == null)
            {
                throw new KOSException("FREECAM:MOVE argument must be a Vector delta.");
            }

            controller.SetSharedObjects(shared);
            controller.Move(delta.ToVector3());
        }

        private Structure GetAnchor()
        {
            switch (controller.AnchorMode)
            {
                case FreeCameraAnchor.Part:
                    var part = controller.AnchorPart;
                    if (part == null)
                    {
                        throw new KOSException("FREECAM:ANCHOR part is unavailable because the anchor part no longer exists.");
                    }

                    return Suffixed.Part.PartValueFactory.Construct(part, shared);
                case FreeCameraAnchor.Body:
                    var body = controller.AnchorBody;
                    if (body == null)
                    {
                        throw new KOSException("FREECAM:ANCHOR body is unavailable because no celestial body is available.");
                    }

                    return BodyTarget.CreateOrGetExisting(body, shared);
                case FreeCameraAnchor.Ship:
                default:
                    var vessel = controller.AnchorVessel;
                    if (vessel == null)
                    {
                        throw new KOSException("FREECAM:ANCHOR vessel is unavailable because no vessel is available.");
                    }

                    return VesselTarget.CreateOrGetExisting(vessel, shared);
            }
        }

        private void SetAnchor(Structure value)
        {
            EnsureFlightScene("ANCHOR");
            if (value == null)
            {
                throw new KOSException("FREECAM:ANCHOR must be a Vessel, Part, Body, or anchor string.");
            }

            controller.SetSharedObjects(shared);

            try
            {
                var anchorString = value as StringValue;
                if (anchorString != null)
                {
                    controller.SetAnchor(anchorString.ToString());
                    return;
                }

                var vessel = value as VesselTarget;
                if (vessel != null)
                {
                    if (vessel.Vessel == null)
                    {
                        throw new KOSException("FREECAM:ANCHOR vessel is unavailable.");
                    }

                    controller.SetAnchor(vessel.Vessel);
                    return;
                }

                var part = value as Suffixed.Part.PartValue;
                if (part != null)
                {
                    if (part.Part == null)
                    {
                        throw new KOSException("FREECAM:ANCHOR part is unavailable.");
                    }

                    controller.SetAnchor(part.Part);
                    return;
                }

                var body = value as BodyTarget;
                if (body != null)
                {
                    if (body.Body == null)
                    {
                        throw new KOSException("FREECAM:ANCHOR body is unavailable.");
                    }

                    controller.SetAnchor(body.Body);
                    return;
                }
            }
            catch (System.ArgumentException ex)
            {
                throw new KOSException(ex.Message);
            }

            throw new KOSException("FREECAM:ANCHOR must be a Vessel, Part, Body, or anchor string.");
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
