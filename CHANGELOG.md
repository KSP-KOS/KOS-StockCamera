kOS Stock Camera Addon Changelog
********************************
# v0.3.1 unreleased 2
- Added `ADDONS:CAMERA:LIGHT` for adding and controlling a light source attached to the camera. See updated README.md for details.
- Freecamera adjustments:
	- Made `:anchor` accept `VESSEL`, `PART` and `BODY` structures instead of strings.
	- Anchoring to parts is now accepted, if the part gets destroyed or unloaded the anchor should revert to the vessel if possible.
	- Removed `:anchorvessel` because of the above change
	- Added `:lookat(position)` 
	- Added `:move(vector)`
	- Attempted to make :anchor to a body more stable/less jittery when the vessel is at high altitude
	- renamed `:orientation` suffix to `:facing` to stay more consistent with kOS terminology
	- `:facing` now accepts both directions and vectors, if vector is used then the roll of the camera will be "up"/horizontal
	- `:setpose()`'s facing parameter also accepts both direction and vector, similar to the above.
	- added `:distance` which can also be set to adjust how far the camera is from the vessel.
- added aliases `addons:camera:flight`, `addons:camera:map`, `addons:camera:internal`, `addons:camera:free`


# v0.3.0 unreleased
- Added new camera mode `ADDONS:CAMERA:FREECAMERA` that comes with several new additions to controlling the camera. See updated README.md for details.

# v0.2.0 2021-01-11

- Update for KSP 1.8 - 1.11
- Add `CAMERA:MAPCAMERA` suffix to control camera controls for mapview

# v0.1.2 2020-09-18

- Update for KSP 1.8 - 1.10

# v0.1.1 2017-10-31

- Update for KSP 1.3.1
- Add `POSITIONUPDATER` suffix to use a delegate to set the camrea position

# v0.1.0 2017-01-11

Initial public release
