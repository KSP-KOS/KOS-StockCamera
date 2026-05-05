using System;
using kOS.Safe.Encapsulation;
using kOS.Safe.Encapsulation.Suffixes;
using kOS.Safe.Utilities;

namespace kOS.AddOns.StockCamera
{
    [kOSAddon("CAMERA")]
    [KOSNomenclature("CAMERAAddon")]
    public class Addon : Suffixed.Addon
    {
        private FlightCameraValue flightCam;
		private MapCameraValue mapCam;
        private InternalCameraValue ivaCam;
        private FreeCameraValue freeCam;

        public Addon(SharedObjects shared) : base(shared)
        {
            AddSuffix("FLIGHTCAMERA", new Suffix<FlightCameraValue>(GetFlightCamera));
			AddSuffix("MAPCAMERA", new Suffix<MapCameraValue>(GetMapCamera));
            AddSuffix("INTERNALCAMERA", new Suffix<InternalCameraValue>(GetInternalCamera));
            AddSuffix("FREECAMERA", new Suffix<FreeCameraValue>(GetFreeCamera));
        }

		public override BooleanValue Available()
        {
            return BooleanValue.True;
        }

        public FlightCameraValue GetFlightCamera()
        {
            if (flightCam == null)
            {
                flightCam = new FlightCameraValue(shared);
            }
            return flightCam;
        }

		private MapCameraValue GetMapCamera()
		{
			if (mapCam == null)
			{
				mapCam = new MapCameraValue(shared);
			}
			return mapCam;
		}

        private FreeCameraValue GetFreeCamera()
        {
            if (freeCam == null)
            {
                freeCam = new FreeCameraValue(shared);
            }
            return freeCam;
        }

        private InternalCameraValue GetInternalCamera()
        {
            if (ivaCam == null)
            {
                ivaCam = new InternalCameraValue(shared);
            }
            return ivaCam;
        }
	}
}