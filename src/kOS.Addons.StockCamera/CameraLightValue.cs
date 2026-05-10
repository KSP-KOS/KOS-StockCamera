using System.Globalization;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Exceptions;
using kOS.Safe.Utilities;
using kOS.Suffixed;
using UnityEngine;

namespace kOS.AddOns.StockCamera
{
    [KOSNomenclature("CameraLight")]
    public class CameraLightValue : Structure
    {
        private readonly CameraLightController controller;

        public CameraLightValue()
        {
            controller = CameraLightController.Instance;

            AddSuffix("ENABLED", new SetSuffix<BooleanValue>(GetEnabled, SetEnabled));
            AddSuffix("ACTIVE", new Suffix<BooleanValue>(GetActive));
            AddSuffix("AVAILABLE", new Suffix<BooleanValue>(GetAvailable));
            AddSuffix("STATUS", new Suffix<StringValue>(GetStatus));

            AddSuffix("INTENSITY", new SetSuffix<ScalarValue>(GetIntensity, SetIntensity));
            AddSuffix(new string[] { "RANGE", "FALLOFF" }, new SetSuffix<ScalarValue>(GetRange, SetRange));
            AddSuffix(new string[] { "ANGLE", "FOV" }, new SetSuffix<ScalarValue>(GetAngle, SetAngle));
            AddSuffix("DISTANCE", new SetSuffix<ScalarValue>(GetDistance, SetDistance));
            AddSuffix(new string[] { "SHADOWS", "SHADOW" }, new SetSuffix<BooleanValue>(GetShadows, SetShadows));

            AddSuffix(new string[] { "COLOR", "COLOUR" }, new SetSuffix<RgbaColor>(GetColor, SetColor));
            AddSuffix(new string[] { "RED", "R" }, new SetSuffix<ScalarValue>(GetRed, SetRed));
            AddSuffix(new string[] { "GREEN", "G" }, new SetSuffix<ScalarValue>(GetGreen, SetGreen));
            AddSuffix(new string[] { "BLUE", "B" }, new SetSuffix<ScalarValue>(GetBlue, SetBlue));
        }

        public override string ToString()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "CameraLight(ENABLED={0}, ACTIVE={1}, INTENSITY={2:0.###}, RANGE={3:0.###}, ANGLE={4:0.###}, DISTANCE={5:0.###}, COLOR=RGBA({6:0.###},{7:0.###},{8:0.###},1), SHADOWS={9})",
                controller.RequestedEnabled,
                controller.Active,
                controller.Intensity,
                controller.Range,
                controller.Angle,
                controller.Distance,
                controller.Red,
                controller.Green,
                controller.Blue,
                controller.ShadowsEnabled);
        }

        private BooleanValue GetEnabled()
        {
            return controller.RequestedEnabled ? BooleanValue.True : BooleanValue.False;
        }

        private void SetEnabled(BooleanValue value)
        {
            controller.SetRequestedEnabled(value);
        }

        private BooleanValue GetActive()
        {
            return controller.Active ? BooleanValue.True : BooleanValue.False;
        }

        private BooleanValue GetAvailable()
        {
            return controller.Available ? BooleanValue.True : BooleanValue.False;
        }

        private StringValue GetStatus()
        {
            return new StringValue(controller.Status);
        }

        private ScalarValue GetIntensity()
        {
            return ScalarValue.Create(controller.Intensity);
        }

        private void SetIntensity(ScalarValue value)
        {
            var scalar = (float)value.GetDoubleValue();
            if (scalar < 0f)
            {
                throw new KOSException("CAMERA:LIGHT:INTENSITY must be zero or greater.");
            }

            controller.Intensity = scalar;
        }

        private ScalarValue GetRange()
        {
            return ScalarValue.Create(controller.Range);
        }

        private void SetRange(ScalarValue value)
        {
            var scalar = (float)value.GetDoubleValue();
            if (scalar <= 0f)
            {
                throw new KOSException("CAMERA:LIGHT:RANGE must be greater than zero.");
            }

            controller.Range = scalar;
        }

        private ScalarValue GetAngle()
        {
            return ScalarValue.Create(controller.Angle);
        }

        private void SetAngle(ScalarValue value)
        {
            var scalar = (float)value.GetDoubleValue();
            if (scalar <= 0f || scalar >= 180f)
            {
                throw new KOSException("CAMERA:LIGHT:ANGLE must be greater than 0 and less than 180 degrees.");
            }

            controller.Angle = scalar;
        }

        private ScalarValue GetDistance()
        {
            return ScalarValue.Create(controller.Distance);
        }

        private void SetDistance(ScalarValue value)
        {
            var scalar = (float)value.GetDoubleValue();
            if (scalar < 0f)
            {
                throw new KOSException("CAMERA:LIGHT:DISTANCE must be zero or greater.");
            }

            controller.Distance = scalar;
        }

        private BooleanValue GetShadows()
        {
            return controller.ShadowsEnabled ? BooleanValue.True : BooleanValue.False;
        }

        private void SetShadows(BooleanValue value)
        {
            controller.ShadowsEnabled = value;
        }

        private RgbaColor GetColor()
        {
            return new RgbaColor(controller.Red, controller.Green, controller.Blue, 1f);
        }

        private void SetColor(RgbaColor value)
        {
            if (value == null)
            {
                throw new KOSException("CAMERA:LIGHT:COLOR must be a kOS Color.");
            }

            var color = value.Color;
            var newRed = ValidateColorChannel(color.r, "COLOR:RED");
            var newGreen = ValidateColorChannel(color.g, "COLOR:GREEN");
            var newBlue = ValidateColorChannel(color.b, "COLOR:BLUE");
            controller.SetColor(newRed, newGreen, newBlue);
        }

        private ScalarValue GetRed()
        {
            return ScalarValue.Create(controller.Red);
        }

        private void SetRed(ScalarValue value)
        {
            controller.Red = GetColorChannel(value, "RED");
        }

        private ScalarValue GetGreen()
        {
            return ScalarValue.Create(controller.Green);
        }

        private void SetGreen(ScalarValue value)
        {
            controller.Green = GetColorChannel(value, "GREEN");
        }

        private ScalarValue GetBlue()
        {
            return ScalarValue.Create(controller.Blue);
        }

        private void SetBlue(ScalarValue value)
        {
            controller.Blue = GetColorChannel(value, "BLUE");
        }

        private static float GetColorChannel(ScalarValue value, string suffixName)
        {
            return ValidateColorChannel((float)value.GetDoubleValue(), suffixName);
        }

        private static float ValidateColorChannel(float value, string suffixName)
        {
            if (value < 0f || value > 1f)
            {
                throw new KOSException("CAMERA:LIGHT:" + suffixName + " must be between 0 and 1.");
            }

            return value;
        }
    }
}
