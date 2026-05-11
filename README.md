kOS Stock Camera Addon
**********************

# Description

This project is an addon for [kOS](https://github.com/KSP-KOS/KOS), which is a
mod for the game [Kerbal Space Program](https://kerbalspaceprogram.com/). It
provides scriptable access to KSP camera controls from KerboScript.

The addon exposes both stock-camera helpers and an optional free-camera mode:

* `FLIGHTCAMERA` controls KSP's normal stock flight camera behavior, including
  camera mode, target, distance, heading, pitch, position, and FOV.
* `MAPCAMERA` controls map-view camera behavior.
* `INTERNALCAMERA` controls IVA/internal camera behavior.
* `FREECAMERA` temporarily takes ownership of KSP's `FlightCamera` so scripts
  can set an exact camera pose, including position, facing, roll, anchor
  behavior, and vessel-relative camera frames.
* `LIGHT` creates a camera-mounted spot light that follows the active gameplay
  camera, including stock flight camera, free camera, map camera, and IVA when
  a compatible render camera is available.

The "stock" designation remains important. This addon works with KSP's existing
camera systems. Other mods that replace or heavily modify camera behavior may
conflict with this functionality.

# Quick start

Existing stock flight-camera usage:

```kerboscript
set cam to addons:camera:flightcamera.
set cam:mode to "FREE".
set cam:fov to 45.
set cam:distance to 40.
set cam:heading to 90.
set cam:pitch to 10.
```

Free-camera usage:

```kerboscript
set cam to addons:camera:freecamera.
set cam:enabled to true.
set cam:fov to 35.
set cam:anchor to ship.
cam:setpose(V(0, 12, -35), heading(90, 10, 0)).
wait 5.
set cam:enabled to false.
```

`FLIGHTCAMERA` and `FREECAMERA` are intentionally separate. Use
`FLIGHTCAMERA` for normal stock camera control. Use `FREECAMERA` when a script
needs full-pose control of the flight camera. Avoid driving both from
one script at the same time.

Camera light usage:

```kerboscript
set light to addons:camera:light.
set light:enabled to true.
set light:intensity to 1.5.
set light:range to 80.
set light:angle to 45.
```

# Structures

## CAMERA

The root addon is available as:

```kerboscript
addons:camera
```

Suffixes:

| Suffix | Type | Get/Set | Description |
|---|---:|:---:|---|
| `FLIGHTCAMERA` / `FLIGHT` | `FlightCamera` | Get | Returns the object which allows control of the stock camera in the flight scene. |
| `MAPCAMERA` /  `MAP` | `MapCamera` | Get | Returns the object which allows control of the camera in map view. |
| `INTERNALCAMERA` / `INTERNAL` | `InternalCamera` | Get | Returns the object which allows control of the IVA/internal camera. |
| `FREECAMERA` / `FREE` | `FreeCamera` | Get | Returns the object which allows temporary full-pose control of the flight camera. |
| `CAMERALIGHT` / `LIGHT` | `CameraLight` | Get | Returns the object which controls a camera-mounted spot light. |

## FLIGHTCAMERA

`FLIGHTCAMERA` controls KSP's normal flight camera. It does not replace the
stock camera update model; it changes the same fields KSP normally uses.

| Suffix | Type | Get/Set | Description |
|---|---:|:---:|---|
| `MODE`<br>`CAMERAMODE` | `String` | Get/Set | Returns or changes the selected camera mode. Valid options are `"AUTO"`, `"CHASE"`, `"FREE"`, `"LOCKED"`, and `"ORBITAL"`. |
| `FOV`<br>`CAMERAFOV` | `Scalar` | Get/Set | Returns or sets the field of view for the flight camera. |
| `PITCH`<br>`CAMERAPITCH` | `Scalar` | Get/Set | Returns or sets the pitch component of the camera position rotation. The actual direction depends on the frame of reference of the current camera mode. |
| `HEADING`<br>`HDG`<br>`CAMERAHDG` | `Scalar` | Get/Set | Returns or sets the yaw component of the camera position rotation. The actual direction depends on the frame of reference of the current camera mode. |
| `DISTANCE`<br>`CAMERADISTANCE` | `Scalar` | Get/Set | Returns or sets the distance component of the camera position, the magnitude applied to the rotation defined by pitch and heading. |
| `POSITION`<br>`CAMERAPOSITION` | `Vector` | Get/Set | Returns or sets the camera's position using a CPU-vessel-centered vector. The pitch, heading, and distance components are automatically calculated from the vector. Changing the camera target does not change the reference origin; always set this using vectors based on the CPU vessel. |
| `TARGET` | `Part` or `Vessel` | Get/Set | Returns or sets the vessel or part that the camera is pointing at. This is the same as KSP's "Aim here" feature. |
| `TARGETPOS` | `Vector` | Get | Debugging value. |
| `PIVOTPOS` | `Vector` | Get | Debugging value. |
| `POSITIONUPDATER` | `UserDelegate` | Get/Set | A delegate automatically called once per tick to update the camera position. Initially this returns a `DONOTHING` delegate. Set it back to `DONOTHING` to stop automatic position updates. |

## MAPCAMERA

`MAPCAMERA` controls the map-view camera.

| Suffix | Type | Get/Set | Description |
|---|---:|:---:|---|
| `SETFILTER(string, boolean)` | Function | � | Sets whether objects of a given type should be visible in map mode. See `FILTERNAMES` for valid names. |
| `GETFILTER(string)` | Function | � | Returns whether objects of the specified type are visible. See `FILTERNAMES` for valid names. |
| `COMMNETMODE` | `String` | Get/Set | Gets or sets the current commnet display mode. See `COMMNETNAMES` for valid modes. |
| `PITCH`<br>`CAMERAPITCH` | `Scalar` | Get/Set | Gets or sets the pitch angle of the camera, relative to the ecliptic plane. |
| `HDG`<br>`HEADING`<br>`CAMERAHDG` | `Scalar` | Get/Set | Gets or sets the camera heading/yaw. |
| `DISTANCE`<br>`CAMERADISTANCE` | `Scalar` | Get/Set | Returns the camera distance from the camera pivot, in meters. Map view may enforce limits on this value. |
| `POSITION`<br>`CAMERAPOSITION` | `Vector` | Get/Set | Gets or sets the position of the camera in SHIP-RAW coordinates. The camera will always face toward the pivot point. |
| `TARGET` | `Vessel`, `Body`, or `Node` | Get/Set | Gets or sets the pivot object. |
| `FILTERNAMES` | `List` | Get | Returns the valid filter names for use with `SETFILTER` and `GETFILTER`. |
| `COMMNETNAMES` | `List` | Get | Returns the valid commnet display mode names for use with `COMMNETMODE`. |

## INTERNALCAMERA

`INTERNALCAMERA` controls the IVA/internal camera when IVA is active.

| Suffix | Type | Get/Set | Description |
|---|---:|:---:|---|
| `PITCH`<br>`CAMERAPITCH` | `Scalar` | Get/Set | Gets or sets the IVA camera pitch, clamped to the limits of the active internal camera. |
| `ROT`<br>`ROTATION`<br>`CAMERAROTATION` | `Scalar` | Get/Set | Gets or sets the IVA camera rotation, clamped to the limits of the active internal camera. |
| `FOV`<br>`CAMERAFOV` | `Scalar` | Get/Set | Gets or sets the IVA camera field of view. |
| `ACTIVEKERBAL` | `CrewMember` | Get/Set | Gets or sets the active IVA Kerbal. The Kerbal must be on the active vessel. |
| `ACTIVE` | `Boolean` | Get | True when the current camera mode is IVA. |


## FREECAMERA

`FREECAMERA` is for scripted full-pose camera control in the flight scene.
Unlike `FLIGHTCAMERA`, it temporarily takes ownership of KSP's `FlightCamera`
while enabled, then restores the stock camera when disabled or when KSP changes
to an incompatible camera context.

Access:

```kerboscript
set cam to addons:camera:freecamera.
```

Suffixes:

| Suffix | Type | Get/Set | Description |
|---|---:|:---:|---|
| `ENABLED` | `Boolean` | Get/Set | Enables or disables free-camera control. Disabling restores the stock flight camera. |
| `ACTIVE` | `Boolean` | Get | True when `FREECAMERA` currently owns the flight camera. |
| `AVAILABLE` | `Boolean` | Get | True when the current scene has a usable `FlightCamera`. |
| `STATUS` | `String` | Get | Human-readable status/debug text. |
| `FOV` | `Scalar` | Get/Set | Camera field of view in degrees. Restored to the stock FOV when freecam is disabled. |
| `POSITION` | `Vector` | Get/Set | Camera position in SHIP-RAW coordinates. |
| `DISTANCE` | `Scalar` | Get/Set | Distance from the CPU vessel's CoM. Gets the same value as `cam:position:mag`; setting scales the current `POSITION` direction to the requested distance. |
| `FACING` | `Direction` or `Vector` | Get/Set | Camera facing. Gets a kOS `Direction`; accepts a `Direction` or non-zero `Vector` look direction when set. Vector assignment uses the camera's body-up vector as the effective up direction. |
| `HEADING`<br>`HDG` | `Scalar` | Get/Set | Local-horizon heading in degrees. `0` is north and `90` is east. |
| `PITCH` | `Scalar` | Get/Set | Local-horizon pitch in degrees. Positive looks upward. |
| `ROLL` | `Scalar` | Get/Set | Roll around the camera's forward axis, in degrees. |
| `ANCHOR` | `Vessel`, `Part`, `Body`, or `String` | Get/Set | Anchor target. Gets the current anchor object; accepts a vessel, part, body, or string shorthand such as `"SHIP"`/`"VESSEL"` or `"BODY"`. |
| `ANCHORFRAME` | `String` | Get/Set | Ship anchor frame. Valid values are `"RAW"` and `"FACING"`. |
| `SETPOSE(position, facing)` | Function | — | Sets `POSITION` and `FACING` together. The facing argument may be a `Direction` or non-zero `Vector` look direction. Vector facing uses the body-up vector at the new camera position. |
| `LOOKAT(targetPosition)` | Function | — | Points the camera at a SHIP-RAW target position without moving it. Uses the camera's body-up vector as the effective up direction. |
| `MOVE(delta)` | Function | — | Adds a SHIP-RAW vector delta to `POSITION`. Equivalent to `set cam:position to cam:position + delta`. |
| `COPYFROMSTOCK()` | Function | — | Copies the current stock flight camera pose and FOV into freecam state. |
| `RESET()` | Function | — | Resets freecam to the saved stock pose when available. |



### FREECAMERA anchor behavior

`ANCHOR` controls how the desired camera pose is preserved over time. It accepts
actual kOS target objects as well as string shorthands:

```kerboscript
set cam:anchor to ship.              // vessel anchor
set cam:anchor to ship:rootpart.     // part anchor
set cam:anchor to body.              // body anchor, when body is a BodyTarget
set cam:anchor to "SHIP".            // shorthand for the CPU/active vessel
set cam:anchor to "BODY".            // shorthand for the CPU/active vessel's body
```

When anchored to a vessel, the camera follows the vessel center of mass. With
`ANCHORFRAME = "RAW"`, it keeps a fixed raw-axis offset from the vessel. With
`ANCHORFRAME = "FACING"`, it stores both position and facing relative to the
vessel's current facing.

When anchored to a part, the camera position and facing are stored in the part's
local transform space. This is useful for root-part, cockpit, wing, or other
attached cameras because the camera stays fixed relative to the selected part
even when the vessel center of mass shifts due to fuel burn, staging, docking,
or cargo changes. If the part is decoupled or undocked into a new loaded vessel,
the camera continues following that same part. If the part becomes unavailable
because it is destroyed or its vessel unloads, freecam preserves the current
visual pose and falls back to a vessel anchor using the part's vessel when
available, otherwise the CPU vessel. `ANCHORFRAME` does not change part-anchor
behavior.

When anchored to a body, the camera position is stored in the celestial body's
local transform space. This is useful for runway, launchpad, flyby, or
terrain-fixed shots. BODY anchors also apply a narrow render-time correction for
the active flight camera, re-resolving the body-local pose immediately before
rendering to reduce jitter from late floating-origin/Krakensbane corrections.

Changing `ANCHOR` preserves the current visual/world camera pose, then rebuilds
the internal representation for the new anchor target.

`ANCHORFRAME = "RAW"` is the default vessel-anchor behavior:

```kerboscript
set cam:anchor to ship.
set cam:anchorframe to "RAW".
set cam:position to V(...).
```

`ANCHORFRAME = "FACING"` is useful for low-lag onboard, chase, or wing cameras
anchored to a vessel:

```kerboscript
set cam:anchor to ship.
cam:setpose(ship:facing:starvector * 2
          + ship:facing:vector     * -1
          + ship:facing:topvector  * 0.1,
            ship:facing).
set cam:anchorframe to "FACING".
```

For a camera fixed to a specific part, set the pose and anchor to the part:

```kerboscript
cam:setpose(ship:facing:starvector * 2
          + ship:facing:vector     * -1
          + ship:facing:topvector  * 0.1,
            ship:facing).
set cam:anchor to ship:rootpart.
```

### FREECAMERA facing behavior

You can use local-horizon heading, pitch, and roll:

```kerboscript
set cam:heading to 90.
set cam:pitch to 15.
set cam:roll to 0.
```

Or assign a kOS `Direction`, or a non-zero look-direction `Vector`, directly:

```kerboscript
set cam:facing to ship:facing.
set cam:facing to heading(90, 15, 0).
set cam:facing to ship:facing:vector.
```

When `FACING` is set from a vector, the vector is treated as a look direction,
not a target position. The camera's effective up direction is the body-up vector
at the camera position, equivalent to:

```kerboscript
set cam:facing to lookdirup(<vector>, cam:position - body:position).
```

Use `LOOKAT(targetPosition)` for point-target behavior. `LOOKAT` interprets its argument as a SHIP-RAW point, not a look direction:

```kerboscript
cam:lookat(target:position).
```

`LOOKAT` changes facing only; it does not move the camera or change the current anchor settings.

`SETPOSE(position, facing)` is equivalent to setting `POSITION` and
`FACING`, but applies both values as one pose update. The facing argument may
be a `Direction` or a non-zero `Vector` look direction. When the facing
argument is a vector, its effective up direction is calculated at the new
`position`:

```kerboscript
cam:setpose(V(0, 12, -35), heading(90, 10, 0)).
```

Use `SETPOSE` when changing both position and facing in the same script
step.

`MOVE(delta)` is a shorthand for adding a SHIP-RAW vector to the current camera
position:

```kerboscript
cam:move(ship:facing:vector * -5).
```

### FREECAMERA scene and IVA behavior

`FREECAMERA` controls KSP's existing `FlightCamera`; it does not create a
separate Unity camera stack.

When KSP changes away from the normal flight camera, freecam releases control.
Map-view transitions may suspend and later resume freecam behavior. IVA/internal
camera mode is treated differently: selecting IVA disables freecam and defers
`FlightCamera` restore/reparent work until KSP is safely back in flight camera
mode. This avoids corrupting the first IVA camera frame.

The controller normally applies pose updates from `LateUpdate`. BODY anchors also
re-apply their body-local pose from `Camera.onPreCull`, but only for the active
flight camera while freecam is active in Flight camera mode. This targeted
render-time correction is intended to keep stationary/body-fixed cameras stable
when KSP performs late floating-origin or Krakensbane corrections.

## LIGHT

`LIGHT` controls a Unity spot light that is kept just behind the active gameplay
camera and pointed the same way as the camera. It is intended as a scriptable
camera-mounted flashlight/fill light for night shots or dark interiors.

Access:

```kerboscript
set light to addons:camera:light.
```

Suffixes:

| Suffix | Type | Get/Set | Description |
|---|---:|:---:|---|
| `ENABLED` | `Boolean` | Get/Set | Enables or disables the camera light. |
| `ACTIVE` | `Boolean` | Get | True when the Unity light exists and is currently enabled for a render camera. |
| `AVAILABLE` | `Boolean` | Get | True when the addon can find a likely gameplay camera to follow. |
| `STATUS` | `String` | Get | Human-readable status/debug text. |
| `INTENSITY` | `Scalar` | Get/Set | Unity light intensity. Must be zero or greater. Default is `1`. |
| `RANGE`<br>`FALLOFF` | `Scalar` | Get/Set | Unity light range in meters; this is the falloff distance. Must be greater than zero. Default is `50`. |
| `ANGLE`<br>`FOV` | `Scalar` | Get/Set | Spot-light outer cone angle in degrees. Must be greater than `0` and less than `180`. Default is `45`. |
| `DISTANCE` | `Scalar` | Get/Set | Distance behind the camera where the light origin is placed. Must be zero or greater. Default is `0.25`. |
| `SHADOWS`<br>`SHADOW` | `Boolean` | Get/Set | Enables or disables Unity soft shadows for the light. Default is `false`. |
| `COLOR`<br>`COLOUR` | `RGBA` | Get/Set | kOS color structure. Use values like `RGB(1, 0.92, 0.82)` or `WHITE`. Alpha is ignored.  |
| `RED`<br>`R` | `Scalar` | Get/Set | Red color channel from `0` to `1`. Default is `1`. |
| `GREEN`<br>`G` | `Scalar` | Get/Set | Green color channel from `0` to `1`. Default is `0.92`. |
| `BLUE`<br>`B` | `Scalar` | Get/Set | Blue color channel from `0` to `1`. Default is `0.82`. |

The default color is slightly warm white: `RGB(1, 0.92, 0.82)`. Printing `addons:camera:light` shows a one-line summary of the current light settings.

Example:

```kerboscript
set light to addons:camera:light.
set light:enabled to true.
set light:intensity to 2.
set light:range to 100.
set light:angle to 35.
set light:distance to 0.5.
set light:color to RGB(1, 0.92, 0.82).
set light:shadows to false.
```


# Building

You must have an IDE or compiler capable of building Visual Studio solutions
(`.sln` files). For the sake of simplicity, this repository assumes that it will
be located next to the kOS repository, in the same parent directory. All
references match those of kOS, and use relative paths pointing to the files in
the kOS repository.
