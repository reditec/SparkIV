/**********************************************************************\

 SparkIV
 Copyright (C) 2026  reditec

 This program is free software: you can redistribute it and/or modify
 it under the terms of the GNU General Public License as published by
 the Free Software Foundation, either version 3 of the License, or
 (at your option) any later version.

 This program is distributed in the hope that it will be useful,
 but WITHOUT ANY WARRANTY; without even the implied warranty of
 MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 GNU General Public License for more details.

 You should have received a copy of the GNU General Public License
 along with this program.  If not, see <http://www.gnu.org/licenses/>.

\**********************************************************************/

"use strict";
// GTA IV (PC) for the model view (ModelView.html): what the page draws as that game and version draws it, taken from
// its shaders (common\shaders\win32_30\*.fxc), timecyc.dat and GTAIV.exe. The page puts the GLSL parts (GameGlsl) into
// its shaders and calls the rest (the water simulation, the sea, the lamps outside) by name; another game or engine
// version brings its own file with the same names. SparkIV serves it before the page's own script.

// How many lamps' shadow maps a frame draws (see lampShadowTarget).
const LampShadowSlots = 4;

const GameGlsl = Object.freeze({
  // The fragment shader's uniforms for the parts below.
  uniforms: `
    // How bright the water's reflection is (water.fxc's waterReflectionScale: the time cycle's, see TimeCycle.cs).
    uniform float waterReflect;
    // The picture's gamma (rage_postfx.fxc's deSatContrastGamma.z: the time cycle's Gamma).
    uniform float toneGamma;
    // The rest of the picture's tone mapping (rage_postfx.fxc, see gameToneMap): MidGrey / the adapted
    // luminance, deSatContrastGamma.x, ColorCorrect x 2, ColorShift (w: 1 / its luminance).
    uniform float toneBloom;
    uniform float toneScale; uniform float toneSaturation; uniform vec3 toneCorrect; uniform vec4 toneShift;
    // gDayNightEffects.x: 1 at noon, 0 at midnight (see outsideLight).
    uniform float dayNight;
    // A ped's shine: gDirectionalLight.w, globalScalars.w (see pedShine).
    uniform vec2 pedShineScale;
    // A map's glass, drawn forward, not in the G-buffer (see pedShine).
    uniform bool glassForward;
    // A vehicle shader's shine: intensity factor, power factor, 1 for gta_vehicle_vehglass's forward draw (see
    // vehicleShine; GtaIVPcMaterials.VehicleShineOf).
    uniform vec3 vehicleSpec;
    // The fog (see gameFog): its near colour in light, FogStart, FarClip, NearFogAmount.
    uniform vec3 fogNear; uniform vec3 fogParams;
    // gDepthFxParams (see gameFog): DesaturationFar, GammaFar, DepthFxNear, DepthFxFar; the page's exposure.
    uniform vec4 depthFx; uniform float depthFxExposure;
    // The mirrored surroundings' map (see parabolicVertex): being drawn into it, the camera's place (its centre),
    // the map and whether it is there to read (see mirroredAround).
    uniform bool parabolic; uniform vec3 parabolicAt; uniform sampler2D parabolicMap; uniform bool parabolicOn;
    // The sun's shadow (see gameSunShadow, sunShadowCascades): its map of the four cascades side by side, the
    // light's axes (u, v across, h towards the sun), each cascade's box (centre on u and v, 1 / half its size),
    // the camera's forward axis, the heights' reference (the camera's height along h); drawing into the map
    // (shadowPass) less the cascade's bias.
    uniform bool shadowOn; uniform highp sampler2D shadowMap; uniform vec3 shadowU; uniform vec3 shadowV; uniform vec3 shadowH;
    uniform vec4 shadowBox[4]; uniform vec3 viewForward; uniform float shadowRef; uniform bool shadowPass; uniform float shadowBias;
    uniform bool shadowBlend;
    // The lamps' shadows (see lampShadow, lampShadowTarget): their maps, each light's in it (its slot, -1 none),
    // whether they are there to read; drawing into a lamp's (lampShadowPass) its place and radius.
    uniform highp sampler2D lampShadowMap; uniform float sceneLightShadow[40]; uniform bool lampShadowOn;
    uniform bool lampShadowPass; uniform vec3 lampShadowAt; uniform float lampShadowRange;
  `,
  // The vertex shader drawing into the sun's shadow map (sets gl_Position from p).
  shadowVertex: `
        // As the game's shadow vertex shader (0x247AF240 in the TLAD trace): across, the point on the cascade's
        // u and v (gShadowMatrix x and y); the depth from its height along the sun (the highest kept: 1 - its
        // third column; here 1 km either way of the camera's height).
        float up0 = dot(p.xyz, shadowH) - shadowRef;
        gl_Position = vec4((dot(p.xyz, shadowU) - shadowCascade.x) * shadowCascade.z, (dot(p.xyz, shadowV) - shadowCascade.y) * shadowCascade.w,
                           clamp(-up0 / 1000.0, -1.0, 1.0), 1.0);
  `,
  // The fragment shader drawing into the sun's shadow map (writes outColor and returns).
  shadowFragment: `
        // Its pixel shader (0x23CFA7A0): the height along the sun (gShadowMatrix's fourth column), here less the
        // cascade's bias (the game's cascades' fourth constant is that much lower than the sun pass's).
        // A see-through, decal or cut-out material blended in by its texture's alpha (its own wd_draw, see shadowTarget).
        float stored0 = dot(vPosition, shadowH) - shadowRef - shadowBias;
        outColor = vec4(stored0, 0.0, 0.0, shadowBlend ? clamp(base.a, 0.0, 1.0) : 1.0);
        outLight = vec4(0.0);
        return;
  `,
  // The vertex shader drawing into a lamp's shadow map (sets gl_Position from p; lampShadowSide the half, -1 or 1).
  lampShadowVertex: `
        // As the game draws its peds into a lamp's map each frame (the TLAD trace GTAIV.3, frame 10210 from
        // 102284236: VS 0x25afd220, gShadowMatrix c60-c63 = the lamp's dShadowMatrix, turning the world's x into the
        // axis and y, -z across): of the point's offset L from the lamp in that frame and s its side's sign (the red
        // half +1, the green -1: gShadowParam14151617.y 0 or 1), the place L.xy / (|L| - s L.z) - every point, also
        // the other side's (stretched far out) -; the depth |L| / radius; on to the pixel s L.z / radius and
        // |L| / radius (interpolated, not the pixel's own).
        vec3 lampOff = p.xyz - lampShadowAt;
        vec3 lampL = vec3(lampOff.y, -lampOff.z, lampOff.x);
        float lampLen = length(lampL);
        float lampSide = -lampShadowSide * lampL.z;
        gl_Position = vec4(lampL.xy / max(lampLen - lampSide, 1e-6), lampLen / lampShadowRange * 2.0 - 1.0, 1.0);
        vLampShadow = vec2(lampSide, lampLen) / lampShadowRange;
  `,
  // The fragment shader drawing into a lamp's shadow map (writes outColor and returns).
  lampShadowFragment: `
        // Its pixel shader (0x25d42d60): only what is on its side (s L.z / radius at most 0.01, else killed), the
        // distance over the radius as it came from the vertices (in the trace's map 0.23 to 1 where a ped was, 1
        // where nothing; see lampShadowTarget).
        if (vLampShadow.x > 0.01) discard;
        outColor = vec4(vLampShadow.y, 0.0, 0.0, 1.0);
        outLight = vec4(0.0);
        return;
  `,
  // The vertex shader drawing into the mirrored surroundings' map (sets gl_Position from p).
  parabolicVertex: `
        // As the game draws its map of the surroundings each frame (the reflection pass before the G-buffer: a
        // 1024 x 1024 A16B16G16R16F target, the sky first, then the map's objects - no people, no vehicles -; in the
        // TLAD trace GTAIV.4 frame 9350 the vertex shader 0x22EC8A80 with the view at the camera): of the point's
        // direction d from the camera, len its distance, the place d.xy / (len + d.z) - every direction above the
        // camera inside the circle, straight up in its middle - and the depth 1 - 1 / (len + 1).
        vec3 d = p.xyz - parabolicAt;
        float len = length(d);
        gl_Position = vec4(d.xy / max(len + d.z, 1e-4), 1.0 - 2.0 / (len + 1.0), 1.0);
  `,
  // The fragment shader drawing into the mirrored surroundings' map (writes outColor and returns); n0: the normal.
  parabolicFragment: `
        // As the game's pixel shader for it (0x1A37E8E0 in the trace): nothing below the camera (texkill of the height
        // over it), the texture lit by the light from around x AO and the sun (saturate((n.l - 0.25) x 4/3), no shadow,
        // no lamps), the far fog over it (globalFogColor by saturate((distance - FogStart) / (FarClip - FogStart))).
        // Blended over the sky drawn first (the pass's state: SRCALPHA / INVSRCALPHA) by the texture's alpha x the
        // vertex's x saturate((64 - distance) x 0.25): fading from 60 m, gone from 64 m (globalScalars.x 1).
        if (vPosition.z < parabolicAt.z) discard;
        vec3 around0 = mix(ambientAbove, ambientBelow, clamp(0.5 - 0.5 * n0.z, 0.0, 1.0));
        float sun0 = clamp((dot(n0, sunDir) - 0.25) * 1.3333333, 0.0, 1.0);
        float ao0 = clamp(baked ? dayNight * vColor.r + (1.0 - dayNight) * vColor.g : vColor.r, 0.0, 1.0);
        vec3 lit0 = base.rgb * (around0 * ao0 + sunColor * sun0);
        float distance0 = length(vPosition - parabolicAt);
        float far0 = clamp((distance0 - fogParams.x) / max(fogParams.y - fogParams.x, 1.0), 0.0, 1.0);
        float alpha0 = clamp(base.a * vAlpha, 0.0, 1.0) * clamp((64.0 - distance0) * 0.25, 0.0, 1.0);
        if (alpha0 <= 0.0) discard;
        outColor = vec4(mix(lit0, gameToneUnmap(hazeColor), far0), alpha0);
        outLight = vec4(0.0);
        return;
  `,
  // The light outside on a surface: vec3 outside (before the page's lamps and the cutscene's lights).
  outsideLight: `
      // As deferred_lighting.fxc's sun pass (programs 21 to 24) lights the G-buffer: albedo x (the sun x
      // saturate((n.l - 0.25) x 4/3) + the light from around x AO), the light from around gLightAmbient0 +
      // gLightAmbient1 x saturate(0.5 - 0.5 n.z), the game's gLightAmbient1 being Amb1 - Amb0 (measured in a
      // D3D capture: c37 1.46/3.12/2.95, c38 2.27/0.05/-0.49), so from Amb0 facing up to Amb1 facing down
      // (GTAIV.exe 0xAC2E70, 0xAC51F0: the time cycle's Amb0 x AmbLightMult0, Amb1 x AmbLightMult1; the sun Dir x
      // DirLightMult, 0xAC30F0; c18 measured 1/0.851/0.714 x 10.25). The AO only on the light from around, never
      // on the sun: the map shaders' vertex shader (gta_default program 0) a = gDayNightEffects.x x red +
      // .y x green, the day/night factor 1 at noon and 0 at midnight (0x9CB895). Here in the light's units,
      // times the exposure (the tone mapping divides by it again). The sun x its shadow (gameSunShadow; the
      // sun pass's r0.y, which also takes the sun's shine but not the mirrored surroundings).
      // Peds (the cast) the same, in the same G-buffer and sun pass: their AO is their vertex red alone
      // (gta_ped.fxc program 10 with its vertex shader: 1 + gs2.y x gs2.z x (red x gs.z - 1), the time cycle's
      // gs2.y, gs2.z 1 - no day/night; GTAIV.exe 0xAF3A50, 0xAEFEB0), no light of their own nor any dimming.
      vec3 aroundLight = mix(ambientAbove, ambientBelow, clamp(0.5 - 0.5 * n.z, 0.0, 1.0));
      float sunLit = clamp((dot(n, sunDir) - 0.25) * 1.3333333, 0.0, 1.0);
      // (x the room's AmbientDownMult for what moves: peds, a cutscene's props; see TimeCycle.ActorAmbient)
      // (A tree's 0.5 whatever its vertex: gta_trees' G-buffer pass writes GB2.z = 0.5, see the uniform tree. A solid
      // emissive's 1: gta_emissive's, _strong's and _night's deferred_draw write GB2.z = globalScalars.z, not the
      // vertex colour, which only colours their glow; globalScalars.z taken as 1, 0.996 in the GTA IV traces.
      // A vehicle's the vertex colour's luminance: every gta_vehicle_* deferred_draw writes GB2.z = dot(colour,
      // LuminanceConstants 0.2125/0.7154/0.0721) x globalScalars.z.)
      float ao = (tree ? 0.5 : emissive && !textureAlpha ? 1.0
                : vehicle ? clamp(dot(vColor, vec3(0.2125, 0.7154, 0.0721)), 0.0, 1.0)
                : clamp(baked ? dayNight * vColor.r + (1.0 - dayNight) * vColor.g : vColor.r, 0.0, 1.0)) * actorAmbient;
      // A ped's rim on top, as its G-buffer pass writes it (gta_ped's PS 0x25ee22a0 in the TLAD trace GTAIV.3, Malc's
      // body at 102287002, and gta_ped_skin's): of v.n (the camera's view to the point, the normal after the normal
      // map) rim = 1 + 2 v.n where v.n <= 0 (else 1), x the vertex shader's gs2.w x green, at least 0; GB2.z (the AO
      // the light passes read) = AO + rim², GB2.x (the shine, see pedShine) + 0.5 rim². Where the ped faces the
      // camera nothing, towards its outline up to gs2.w x green: Malc's shirt brighter in patches (its green).
      float pedRim = 0.0;
      if (!baked && !vehicle) {
        float viewN = dot(normalize(vPosition - eyeAt), n);
        pedRim = max((viewN <= 0.0 ? 1.0 + 2.0 * viewN : 1.0) * PedRimScale * vColor.g, 0.0);
        ao = clamp(ao + pedRim * pedRim, 0.0, 1.0);
      }
      float sunShadow = outdoor ? gameSunShadow(vPosition) : 1.0;
      vec3 outside = aroundLight * ao + sunColor * sunLit * sunShadow;
  `,
  // A ped's shine outside: vec3 pedShine, added to the light as the sun pass adds its specular (not x albedo).
  pedShine: `
      // As deferred_lighting.fxc's sun pass with gta_ped's G-buffer (program 10): GB2.x = 0.25 x dot(spec map,
      // specMapIntMask) x specularColorFactor, the power spec map alpha x specularFactor x 0.75; the shine
      // 2 x GB2.x x (the sun x (saturate(R.l) + 1e-4)^power x 2 x gDirectionalLight.w + 10 x globalScalars.w x AO
      // x Fresnel (0.75 (1 - |v.n|)^4 + 0.25) x the mirrored sky x saturate(R.z x 5) x (1 - saturate(distance /
      // 100))). gDirectionalLight.w = max(1, (SkyLightMult + SunMult) / DirLightMult) (GTAIV.exe 0xAC30F0),
      // globalScalars.w = 0.6 x the time cycle's 64th number (0xAC6F70); both in pedShineScale. The mirrored
      // surroundings from the game's map (mirroredAround), looked up along R + v x saturate(distance x 0.02)
      // (normalized), blurred by the power (lod 4 - 4 saturate(power x 512 x dReflectionParams.x), that 1 / 230 in
      // both traces).
      // The map's surfaces with a specular map the same, by what their G-buffer passes write (gta_spec,
      // gta_normal_spec, gta_spec_reflect program 14: GB2.x = 0.5 x dot(spec map, specMapIntMask) x
      // specularColorFactor, GB2.y = sqrt(spec map alpha x specularFactor / 512), so the power spec map alpha x
      // specularFactor; gta_default writes GB2.x 0, no shine): et01_ba's booth's chrome slot box (gta_spec_reflect)
      // mirrors the sky as in the game's sun pass (TLAD trace GTAIV.4, call 145514802, the same program).
      // gta_normal and rage_default the same without a specular map, as from a white one (their deferred_draw: GB2.x =
      // 0.5 x specularColorFactor, GB2.y = sqrt(specularFactor / 512); specFlat).
      vec3 pedShine = vec3(0.0);
      if (outdoor && !vehicle && (useSpecMap || specFlat) && useTexture) {
        vec4 spec = useSpecMap ? texture(specTex, vUv) : vec4(1.0);
        float shineAmount = (baked ? 0.5 : 0.25) * (useSpecMap ? dot(spec.rgb, specMask) : 1.0) * specIntensity + 0.5 * pedRim * pedRim;
        float power = spec.a * specPower * (baked ? 1.0 : 0.75);
        vec3 view = normalize(vPosition - eyeAt);
        vec3 mirroredDir = reflect(view, n);
        float fresnel = 0.75 * pow(1.0 - abs(dot(view, n)), 4.0) + 0.25;
        float away = length(vPosition - eyeAt);
        vec3 mapDir = normalize(mirroredDir + view * clamp(away * 0.02, 0.0, 1.0));
        vec3 skyLight = mirroredAround(mapDir, 4.0 - 4.0 * clamp(power / 230.0, 0.0, 1.0));
        float nearby = 1.0 - clamp(away * 0.01, 0.0, 1.0);
        if (glassForward || forwardLit || (stipple && textureAlpha)) {
          // A map's glass and other see-through materials, and a ped's see-through parts (gta_ped, bucket 1), instead
          // by their own forward pass (gta_glass_spec program 20, the trace's booth panes from call 145517152;
          // gta_spec's and gta_ped's draw the same): dot(spec map, specMapIntMask) x specularColorFactor x (the sun x
          // its shadow x (saturate(R.l) + 1e-4)^(spec map alpha x specularFactor + 1e-4) x gDirectionalLight.w + the
          // lamps' shine, see lampShine); not the G-buffer's halving or the peds' 0.75 on the power, no rim and no
          // mirrored surroundings (they aren't in the G-buffer the sun pass reads).
          float forwardAmount = (useSpecMap ? dot(spec.rgb, specMask) : 1.0) * specIntensity;
          float forwardPower = spec.a * specPower;
          pedShine = forwardAmount * (sunColor * sunShadow * pow(clamp(dot(mirroredDir, sunDir), 0.0, 1.0) + 1e-4, forwardPower + 1e-4) * pedShineScale.x
                   + (forwardLit ? lampShine : vec3(0.0)));
        } else {
          pedShine = 2.0 * shineAmount * (sunColor * sunShadow * pow(clamp(dot(mirroredDir, sunDir), 0.0, 1.0) + 1e-4, power + 1e-4) * 2.0 * pedShineScale.x
                   + 10.0 * pedShineScale.y * ao * fresnel * skyLight * clamp(mapDir.z * 5.0, 0.0, 1.0) * nearby
                   // The lamps' and a cutscene's lights' shine (deferred_lighting program 26: 2 x GB2.x x it).
                   + lampShine);
        }
      }
  `,
  // Functions the shader calls (after its uniforms, before main).
  functions: `
    // globalScalars2.w, the peds' rim strength (see outsideLight; 0.725 measured with x32dbg in GTA IV at 11:05,
    // SUNNY; in the TLAD trace GTAIV.3 the constant isn't set again within a frame).
    // gs2.w (0.725 at run time) would scale the ped's rim (see pedRim); off (0): as derived it puts a bright fringe on
    // hair and outlines that the game's videos don't show (bc_3 Niko; 23.10.), the reading of the shader unconfirmed.
    const float PedRimScale = 0.0;
    // How much of a lamp reaches the point past its shadow (deferred_lighting.fxc program 35, the TLAD trace GTAIV.3
    // draw 102287953): the point 0.1 m off along its normal, in the lamp's frame (see lampShadowVertex); its place in
    // its half's map (red: the axis's negative side), four reads around it turned by a pixel's own angle
    // (frac(vPos . (3, 4.272)) of a turn) at 0.75, 0.25, 0.5 and 1 x dShadowOffsetScale.z (0.0029), each kept in
    // the half; lit where the stored distance is at least the point's (stored - distance x dShadowParam0123.w,
    // -1 / radius, >= 0); the four's mean.
    float lampShadow(float slot, vec3 at, float range, vec3 n) {
      vec3 off = vPosition + n * 0.1 - at;
      vec3 l = vec3(off.y, -off.z, off.x);
      float len = length(l);
      vec2 uv = l.xy / max(abs(l.z) + len, 1e-4) * 0.5 + 0.5;
      float side = l.z <= 0.0 ? 0.0 : 1.0;
      float turn = fract(dot(gl_FragCoord.xy - 0.5, vec2(3.0, 4.272)) * 0.159154937 + 0.5) * 6.28318548 - 3.14159274;
      vec2 cs = vec2(cos(turn), sin(turn)) * 0.0029296875;
      vec2 taps[4] = vec2[4](cs.yx * 0.75, -cs.yx * 0.25, vec2(-cs.x, cs.y) * 0.5, vec2(cs.x, -cs.y));
      float lit = 0.0;
      for (int k = 0; k < 4; k++) {
        vec2 at2 = (clamp(uv + taps[k], 0.0, 0.999) + vec2(side, slot)) / vec2(2.0, ${LampShadowSlots}.0);
        if (texture(lampShadowMap, at2).r - len / range >= 0.0) lit += 0.25;
      }
      return lit;
    }
    // The picture as rage_postfx.fxc's tone mapping makes it of the light (the game's HDR x Exposure, the units
    // of this page's light outside): x = light x MidGrey / adapted luminance, L its luminance, the colour drawn
    // towards L (deSatContrastGamma.x), shifted (ColorShift, none at noon), x ColorCorrect x 2, x saturate(L)^
    // (gamma - 1): by the luminance only, the hue kept.
    const vec3 ToneLuma = vec3(0.2125, 0.7154, 0.0721);
    // Before the curve the game adds its bloom: a blurred quarter-size copy of the exposed light x ToneMapParams.z x
    // 0.25 (rage_postfx.fxc program 12; z = IntensityBloom x 0.5, 1.435 at noon: measured in an apitrace capture,
    // where the picture then matches the game's to 1 in 255). Here the blur not yet: the light itself in its place
    // (alike on large even surfaces; the glow beyond edges is missing).
    vec3 gameToneMap(vec3 light) {
      vec3 x = light * (1.0 + toneBloom) * toneScale;
      float L = dot(x, ToneLuma);
      vec3 c = mix(vec3(L), x, toneSaturation);
      float s = L * toneShift.w;
      c = clamp(s, 0.0, 1.0) * (c - toneShift.rgb * s) + toneShift.rgb * s;
      c *= toneCorrect * 2.0;
      return clamp(c * pow(clamp(L, 1e-5, 1.0), toneGamma - 1.0), 0.0, 1.0);
    }
    // The fog rage_postfx.fxc (program 21) lays on the light before the tone mapping, everywhere but the sky:
    // amount = lerp(far, near, NearFogAmount) + NearFogAmount, near = saturate(d / FogStart), far = saturate((d -
    // FogStart) / (FarClip - FogStart)); its colour from the near fog's (globalFogColorN: the time cycle's sky
    // bottom x SkyLightMult, columns 12-14, 60) to the far fog's (globalFogColor) by far; light + amount x
    // (colour - light). NearFogAmount the 12th number (column 11, / 255; GTAIV.exe 0xAF3A50: the alpha of +0x2C).
    // At noon the near fog grows to 17 % of its bright blue by FogStart (147 m). The far fog's colour (the game's
    // bottomSkyColour x SkyLightMult, from its sky's own values: not derived yet) here the haze's colour.
    vec3 gameToneUnmap(vec3 picture);
    // How much of the fog's colour at a distance.
    float gameFogAmount(float distance) {
      float nearRamp = clamp(distance / fogParams.x, 0.0, 1.0);
      float farRamp = clamp((distance - fogParams.x) / max(fogParams.y - fogParams.x, 1.0), 0.0, 1.0);
      // (the + NearFogAmount only where FogStart is 0 or less: GTAIV.exe 0xAF3A50 sets globalFogParams.z to 0 otherwise)
      return mix(farRamp, nearRamp, fogParams.z) + (fogParams.x > 0.0 ? 0.0 : fogParams.z);
    }
    vec3 gameFog(vec3 light, float distance) {
      // Before the fog, the same pass's depth effect (gDepthFxParams; GTA IV's trace GTAIV.1, PS at 158854264): from
      // DepthFxNear to DepthFxFar the colour desaturated towards DesaturationFar and x luma^(GammaFar - 1), the luma
      // in the game's light (without the page's exposure). Here the water at 16 to 128 m some 5 % lighter.
      if (depthFx.w > depthFx.z) {
        float ramp = clamp((distance - depthFx.z) / (depthFx.w - depthFx.z), 0.0, 1.0);
        float luma = dot(light, vec3(0.2125, 0.7154, 0.0721));
        light = mix(vec3(luma), light, mix(1.0, depthFx.x, ramp)) * pow(luma / depthFxExposure + 1e-7, ramp * (depthFx.y - 1.0));
      }
      float farRamp = clamp((distance - fogParams.x) / max(fogParams.y - fogParams.x, 1.0), 0.0, 1.0);
      vec3 colour = mix(fogNear, gameToneUnmap(hazeColor), farRamp);
      return light + gameFogAmount(distance) * (colour - light);
    }
    // Back from the picture to light (without a colour shift, as at noon; what was clipped stays at 1).
    vec3 gameToneUnmap(vec3 picture) {
      vec3 m = picture / max(toneCorrect * 2.0, vec3(1e-4));
      float L = pow(max(dot(m, ToneLuma), 1e-5), 1.0 / toneGamma);
      vec3 c = m / pow(clamp(L, 1e-5, 1.0), toneGamma - 1.0);
      vec3 x = (c - (1.0 - toneSaturation) * L) / max(toneSaturation, 1e-3);
      return max(x, vec3(0.0)) / (toneScale * (1.0 + toneBloom));
    }
    // The mirrored surroundings in the direction r (light), as deferred_lighting.fxc's sun pass (program 21:
    // ParabSampler, texldl at lod) and gta_vehicle_vehglass (program 8: EnvironmentSampler, lod 0) read the game's
    // map: at 1 - (r.xy / (2 (1 + saturate(r.z))) + 0.5) - here the same place in this page's own orientation of
    // it -, the map's level lod (its four: 1024, 512, 256, 128). Where no map object is, the sky (which the game
    // draws into the map first): its colours back in light. Without the map the sky everywhere.
    // The game's map has four levels only, each made from the one above by its own pass (TLAD trace GTAIV.4 frame
    // 10070, draws 158466112 / 158466153 / ...: pixel shader 0x23c2bd80, one filtered read of level c66.z, times
    // |c66.w|^0.25 with c66.w = 1/2, 1/3, 1/4): level n is the mean of the one above x (1 / (n + 1))^0.25, so
    // 1, 0.841, 0.639, 0.452 of the plain mean. Here the plain means (generateMipmap) times those, a lod past 3
    // held at 3. (Without them a toll booth panel at et01_ba f906, reading lod 2.78 near the map's rim, mirrored
    // twice the game's sky: game 3.7/5.2/6.7 there, the plain mean 7.3/10.8/14.4.)
    vec3 mirroredAround(vec3 r, float lod) {
      vec3 skyLight = gameToneUnmap(mix(skyColor, skyTopColor, clamp(r.z * 2.5, 0.0, 1.0)));
      if (!parabolicOn) return skyLight;
      float level = clamp(lod, 0.0, 3.0);
      vec4 m = textureLod(parabolicMap, 0.5 + r.xy / (2.0 * (1.0 + clamp(r.z, 0.0, 1.0))), level);
      float darker = level < 1.0 ? mix(1.0, 0.8409, level) : level < 2.0 ? mix(0.8409, 0.6389, level - 1.0) : mix(0.6389, 0.4518, level - 2.0);
      return m.rgb * darker + (1.0 - m.a) * skyLight;
    }
    // How much of the sun reaches the point P (0 to 1), as deferred_lighting.fxc's sun pass (program 21) and
    // gta_vehicle_vehglass (program 8) read the game's shadow: the cascade by the point's depth along the view
    // (from 9.5, 20 and 50 m on the next: gFacetCentre, c54 in the TLAD trace less the camera's), its place in it,
    // twelve taps around it (c8 to c13, x gShadowParam18192021.y = 1 / 2048 of the map), each lit where the point is
    // as high along the sun as what is stored there or higher; their share plus 1.5 (distance / 175)^2 (the
    // shadow fading out; none from 175 m: c53.w), saturated.
    const vec2 ShadowTaps[12] = vec2[12](vec2(-0.326212, -0.40581), vec2(-0.840144, -0.07358), vec2(-0.695914, 0.457137),
      vec2(-0.203345, 0.620716), vec2(0.96234, -0.194983), vec2(0.473434, -0.480026), vec2(0.519456, 0.767022),
      vec2(0.185461, -0.893124), vec2(0.507431, 0.064425), vec2(0.89642, 0.412458), vec2(-0.32194, -0.932615), vec2(-0.791559, -0.59771));
    float gameSunShadow(vec3 P) {
      if (!shadowOn) return 1.0;
      float dist = length(P - eyeAt);
      if (dist >= 175.0) return 1.0;
      float d = dot(P - eyeAt, viewForward);
      int k = int(d >= 9.5) + int(d >= 20.0) + int(d >= 50.0);
      vec4 box = shadowBox[k];
      vec2 at = vec2((dot(P, shadowU) - box.x) * box.z, (dot(P, shadowV) - box.y) * box.w) * 0.5 + 0.5;
      vec2 uv = vec2((float(k) + at.x) * 0.25, at.y);
      float up0 = dot(P, shadowH) - shadowRef;
      float lit = 0.0;
      for (int i = 0; i < 12; i++) {
        float stored = texture(shadowMap, uv + ShadowTaps[i] / 2048.0).r;
        lit += up0 >= stored ? 1.0 : 0.0;
      }
      float away = dist / 175.0;
      return clamp(lit / 12.0 + 1.5 * away * away, 0.0, 1.0);
    }
  `,
  // A point or spot light's falloff: float reach from d (its distance) and range.
  lightFalloff: `
        // As the game's deferred_lighting.fxc lights a point or spot light: a = saturate(1 - d²/r²),
        // (a⁴ - 0.1) / 0.9 (bright near it, steeply to nothing towards its radius); a lamp's spot from its
        // outer angle to its inner one, saturate((cos - cos outer) × scale); a cutscene's spot (no inner
        // angle known) softly at its edge.
        float a = clamp(1.0 - (d * d) / (range * range), 0.0, 1.0);
        float reach = max((a * a * a * a - 0.1) / 0.9, 0.0);
  `,
  // A vehicle's shine: vec3 vehicleShine (and the second shine added to base.rgb).
  vehicleShine: `
      // A vehicle's shine in the sun (outside) or the view's light, as the game's vehicle shaders give it to
      // its lighting.
      vec3 vehicleShine = vec3(0.0);
      // vehicleSpec.z: 0 a deferred vehicle shader, 1 the forward glass, 2 a deferred one whose strength is at most 1
      // (gta_vehicle_badges, gta_vehicle_lightsemissive: min(dot x reflectivePowerED x 0.225, 1)).
      bool vehicleGlass = vehicleSpec.z > 0.5 && vehicleSpec.z < 1.5;
      // (The glass also without a spec map, as 1: which texture the game binds to an unset SpecSampler is not
      // traced.)
      if (vehicle && (vehicleGlass || (useSpecMap && useTexture)) && reflectivePower > 0.0) {
        vec4 spec = useSpecMap && useTexture ? texture(specTex, vUv) : vec4(1.0);
        // How strong and how sharp, by the shader (vehicleSpec, from its pixel programs: paint 0.15 and 190,
        // vehicle_mesh 1 and 50, vehglass 0.225 and 190; GtaIVPcMaterials.VehicleShineOf).
        float strength = dot(spec.rgb, specMask) * vehicleSpec.x * reflectivePower;
        if (vehicleSpec.z > 1.5) strength = min(strength, 1.0);
        float power = spec.a * vehicleSpec.y * reflectivePower;
        vec3 view = normalize(vPosition - eyeAt);
        vec3 mirrored = reflect(view, n);
        // The vertex colour's luminance (LuminanceConstants 0.2125, 0.7154, 0.0721) x globalScalars.z: the AO the
        // vehicle shaders write into GB2.z (paint1 program 13) or use themselves (vehglass program 8);
        // globalScalars.z taken as 1 (its default; the value while vehicles draw is not traced).
        float vehicleAo = dot(vColor, vec3(0.2125, 0.7154, 0.0721));
        // The mirrored surroundings: the game's map of them (EnvironmentSampler / ParabSampler, drawn each frame:
        // the sky, then the map's objects above the camera; here the same, see mirroredAround and parabolicPass).
        // (Below the horizon the game's map holds gtaWaterColor; both shaders fade it out with saturate(R.z x 5).)
        if (vehicleGlass) {
          // gta_vehicle_vehglass, technique draw (forward, program 8): f = (1 - |v.n|)^4, the shine's scale
          // s = min(dot x reflectivePowerED x 0.225, 1) x (1 - dirt) x saturate(0.5 f + 0.5); the sun
          // 4 x gDirectionalColour x gDirectionalLight.w x (saturate(R.l) + 1e-4)^(spec.a x reflectivePowerED x
          // 190 + 1e-4) x s; the mirrored map x saturate(5 R.z) x (1.6 f + 0.4) x 10 x 2 globalScalars.w x
          // reflectivePowerED x 1.8 x AO x s² (s is applied twice, r1.y at 0x2176 and 0x2181 of the listing).
          // Both are then divided by the alpha (see the page: the blend multiplies them back, so they show
          // whole however clear the glass is).
          float f = pow(1.0 - abs(dot(view, n)), 4.0);
          float s = min(strength, 1.0) * (1.0 - dirtAmount) * clamp(0.5 * f + 0.5, 0.0, 1.0);
          if (outdoor) {
            vec3 skyLight = mirroredAround(mirrored, 0.0);
            vehicleShine = s * 4.0 * pedShineScale.x * sunColor * sunShadow * pow(clamp(dot(mirrored, sunDir), 0.0, 1.0) + 1e-4, power + 1e-4)
                         + s * s * vehicleAo * skyLight * clamp(mirrored.z * 5.0, 0.0, 1.0) * (1.6 * f + 0.4) * 36.0 * pedShineScale.y * reflectivePower;
          } else {
            // The view's own light (SparkIV's, not the game's): as the sun's term.
            vehicleShine = s * 4.0 * pow(clamp(dot(mirrored, light), 0.0, 1.0) + 1e-4, power + 1e-4) * vec3(1.0);
          }
        } else if (outdoor && vehicleSpec.z > 2.5) {
          // gta_vehicle_badges and _lightsemissive, drawn forward (bucket 1; technique draw): f = (1 - |v.n|)^4,
          // s = min(dot x reflectivePowerED x 0.225, 1) x (1 - dirt) x saturate(0.5 f + 0.5); the sun x
          // gDirectionalLight.w x (saturate(R.l) + 1e-4)^(spec.a x reflectivePowerED x 190 + 1e-4) x its shadow x s;
          // the mirrored map (level 0) x saturate(5 R.z) x (1.6 f + 0.4) x 18 x reflectivePowerED x AO x s² (no
          // globalScalars.w, unlike vehglass); not divided by the alpha.
          float f = pow(1.0 - abs(dot(view, n)), 4.0);
          float s = min(strength, 1.0) * (1.0 - dirtAmount) * clamp(0.5 * f + 0.5, 0.0, 1.0);
          vec3 skyLight = mirroredAround(mirrored, 0.0);
          vehicleShine = s * pedShineScale.x * sunColor * sunShadow * pow(clamp(dot(mirrored, sunDir), 0.0, 1.0) + 1e-4, power + 1e-4)
                       + s * s * vehicleAo * skyLight * clamp(mirrored.z * 5.0, 0.0, 1.0) * (1.6 * f + 0.4) * 18.0 * reflectivePower;
        } else if (outdoor) {
          // Paint and the other deferred vehicle shaders: their G-buffer (paint1 program 13) GB2.x = 0.5 x
          // strength x (1 - dirt) (x matDiffuseColor2.w, taken as 1), GB2.y² x 512 = the power, GB2.z = AO;
          // deferred_lighting.fxc's sun pass (program 21) adds 2 GB2.x x (the sun x (saturate(R.l) + 1e-4)^(power
          // + 1e-4) x 2 gDirectionalLight.w + 10 x globalScalars.w x AO x Fresnel (0.75 (1 - |v.n|)^4 + 0.25) x
          // the mirrored map x saturate(5 R'.z) x (1 - saturate(distance / 100))), with R' for the map R + v x
          // saturate(distance x 0.02), normalized (further away it looks a little more along the view). The map
          // blurred by the power (lod 4 - 4 saturate(power x 512 x dReflectionParams.x), that 1 / 230: GB2.y² x 512
          // is the power).
          // gDirectionalLight.w and globalScalars.w as pedShine has them (pedShineScale). Not shadowed (SparkIV
          // has no sun shadow).
          float k = strength * (1.0 - dirtAmount);
          float distance = length(vPosition - eyeAt);
          vec3 mapDir = normalize(mirrored + view * clamp(distance * 0.02, 0.0, 1.0));
          float fresnel = 0.75 * pow(1.0 - abs(dot(view, n)), 4.0) + 0.25;
          vec3 skyLight = mirroredAround(mapDir, 4.0 - 4.0 * clamp(power / 230.0, 0.0, 1.0));
          vehicleShine = k * (sunColor * sunShadow * pow(clamp(dot(mirrored, sunDir), 0.0, 1.0) + 1e-4, power + 1e-4) * 2.0 * pedShineScale.x
                       + 10.0 * pedShineScale.y * vehicleAo * fresnel * skyLight * clamp(mapDir.z * 5.0, 0.0, 1.0)
                         * (1.0 - clamp(distance * 0.01, 0.0, 1.0)));
        } else {
          // The model view's own light (not the game's): the shine where the surface faces half way between it
          // and the camera, by the shader's strength and power.
          vec3 h = normalize(light - view);
          vehicleShine = strength * (1.0 - dirtAmount) * pow(max(dot(n, h), 0.0), max(power, 1.0)) * vec3(1.0);
        }
        vec3 l = outdoor ? sunDir : light;
        // The second shine, added to the colour as gta_vehicle_paint2 writes it into the game's colour buffer
        // (then lit as the colour is): the sun mirrored in the paint, pow(dot(mirrored view, sun), factor x spec
        // alpha x reflectivity) x factor x dot(spec map, mask) x reflectivity x the colour.
        if (spec2Scale.y > 0.0) {
          float exponent = max(spec2Scale.x * spec.a * reflectivePower, 1.0);
          float glint = dot(spec.rgb, specMask) * reflectivePower * spec2Scale.y * pow(max(dot(mirrored, l), 0.0), exponent);
          base.rgb += spec2Color * glint;
        }
      }
  `,
  // A see-through part kept by stipple: changes a (discards below the alpha test).
  stipple: `
          // A person's see-through part as gta_ped.fxc's forward shader: of the pixels only the share its stipple
          // level keeps (stipple.wtd all_stips: kept of 64 per level, level = alpha x 3.996 - row its whole part,
          // column a quarter of the rest), each blended by alpha. Here as that share of the alpha, without the 8 x 8
          // pattern. (Its pass's alpha test at 100 cuts nothing: the engine's ALPHAFUNC stays ALWAYS, fxc.md.)
          const float kept[16] = float[16](0.0, 6.0, 8.0, 12.0, 11.0, 12.0, 16.0, 24.0, 32.0, 40.0, 48.0, 52.0, 52.0, 56.0, 58.0, 64.0);
          float level = min(a, 1.0) * 3.996;
          int index = int(floor(level)) * 4 + int(floor(fract(level) * 4.0));
          a *= kept[index] / 64.0;
  `,
  // The water: rgb and a from the mirrored scene, the waves and the sun.
  water: `
          // As the game's water.fxc (its main pixel shader): the waves are its water simulation's (waterTex.fxc,
          // run each frame - see waterSimStep), whose texture holds their slopes in blue and alpha, laid over
          // the world three times: every 500 m beyond 50 m off, every 100 m everywhere, every 22 m within 50 m.
          // Two sets of slopes: as they are and four times steeper (the game's x and y of its slopes and its z
          // and w; which of them takes what, see below).
          // The view from the eye to the point a metre above the water, as water.fxc takes it: its vertex shader
          // passes the place + (0, 0, 1) (o3 = r0 + c5.wwx, the GTA IV trace GTAIV.1's VS for PS 0x24641060), its
          // pixel shader takes the view and the distance from that - flatter, so more Fresnel.
          vec3 lifted = vPosition + vec3(0.0, 0.0, 1.0);
          vec3 view = normalize(lifted - eyeAt);
          vec3 lightNormal = vec3(0.0, 0.0, 1.0);
          vec3 mirrorNormal = lightNormal;
          float nearWeight = vColor.b * 0.25;
          float nearField = max(1.0 - dot(eyeAt - lifted, eyeAt - lifted) / 900.0, 0.0) * nearWeight;
          if (waterWaves) {
            vec2 p = vPosition.xy;
            float distance2 = dot(eyeAt - lifted, eyeAt - lifted);
            float near = max(1.0 - distance2 * 0.0004, 0.0);
            vec2 big = texture(waterSim, p * 0.002).zw - 0.5;
            vec2 middle = texture(waterSim, p * 0.01).zw - 0.5;
            vec2 small = (texture(waterSim, p * 0.0454545).zw - 0.5) * near;
            vec2 lightSlope = big * 0.0512 * (1.0 - near) + middle * 0.256 + small * 0.116363637;
            // (The big waves alike in both: water.fxc ps 2 scales all four of that read by 0.0512.)
            vec2 mirrorSlope = big * 0.0512 * (1.0 - near) + middle * 1.024 + small * 0.465454549;
            // The sea's swell (dynamicWaterStep) within 50 m, to both: the slope of its heights 2 m to either
            // side (as the game's vertex normals), faded over its window's last 10 m as the heights are.
            vec2 inWindow = p - dynamicMin;
            float metres = floor(min(min(inWindow.x, 200.0 - inWindow.x), min(inWindow.y, 200.0 - inWindow.y)));
            if (metres >= 1.0) {
              float edge = metres < 10.0 ? (metres + 1.0) * 0.1 : 1.0;
              vec2 at = p / 200.0 + 0.005;
              vec2 swell = vec2(texture(dynamicWater, at - vec2(0.01, 0.0)).r - texture(dynamicWater, at + vec2(0.01, 0.0)).r,
                                texture(dynamicWater, at - vec2(0.0, 0.01)).r - texture(dynamicWater, at + vec2(0.0, 0.01)).r) * edge * near;
              lightSlope += swell;
              mirrorSlope += swell;
            }
            // The near field (water.fxc ps 2's r0.z): within 30 m the mirror's slope drawn towards the dynamic height
            // map's normal - taken as calm, the surface's own, flat - by its vertices' weight (vertex shader o2.w:
            // (min(red⁴ x 16, 1) x (1 - |n.z|^64) x 0.75 + 0.25) x blue, the height map's factor 1 where it lies on the
            // surface; the sea's colour 0 0 1: 0.25) x (1 - d² / 900).
            mirrorSlope *= 1.0 - nearField;
            lightNormal = normalize(vec3(lightSlope, 1.00001));
            mirrorNormal = normalize(vec3(mirrorSlope, 1.00001));
          }
          // How much water lies in front of what is behind it (the view depths' difference; 1 where the scene is
          // nearer or unknown), for the shore below and the see-through at the end.
          float behindDepth = 1.0;
          if (waterBehindOn) {
            vec2 at = gl_FragCoord.xy / vec2(textureSize(waterBehind, 0));
            float behind = texture(waterBehind, at).r * 2.0 - 1.0, front = gl_FragCoord.z * 2.0 - 1.0;
            float n = waterRange.x, f = waterRange.y;
            behindDepth = 2.0 * n * f / (f + n - behind * (f - n)) - 2.0 * n * f / (f + n - front * (f - n));
            if (behindDepth < 0.0) behindDepth = 1.0;
          }
          // As water.fxc takes its normals (the GTA IV trace GTAIV.1's PS 0x24641060): Fresnel and the mirror's shift
          // by the gentle slopes (the light's above), turned towards the view where the water is shallow - (1 -
          // saturate(d / (saturate(depth^1.125 x 0.02) + 0.05)))², the view x -0.5 of that onto the slope; the
          // sun's light and its glint by the steep ones (the mirror's above).
          // (The view depth, not the distance: water.fxc takes the clip w, its vertex shader's o1.z.)
          float viewDepth = max(dot(vPosition - eyeAt, viewForward), 1e-3);
          float shore = 1.0 - clamp(behindDepth / (clamp(pow(viewDepth, 1.125) * 0.02, 0.0, 1.0) + 0.05), 0.0, 1.0);
          shore *= shore;
          vec3 gentleNormal = waterWaves ? normalize(vec3(lightNormal.xy / max(lightNormal.z, 1e-4) - 0.5 * view.xy * shore, 1.00001 - 0.5 * view.z * shore)) : lightNormal;
          // (Where a wave faces away so far that the view meets its back, the game takes 1 - cos as 1: its cmp on
          // -v.n, not the cosine's size - the most Fresnel, 0.8, there.)
          float cosine = max(-dot(view, gentleNormal), 0.0);
          float fresnel = pow(1.0 - cosine, 5.0) * 0.6 + 0.2;
          vec3 bounce = reflect(view, mirrorNormal);
          vec3 mirrored = mix(skyColor, skyTopColor, clamp(bounce.z * 2.5, 0.0, 1.0));
          vec4 mirroredScene = vec4(0.0);
          if (reflectOn) {
            // The mirrored scene where it is, shifted by the waves as water.fxc does: their slope turned to the
            // picture (across, up) times 0.5 / (0.03 x the distance), at most twice; the sky where nothing was drawn.
            vec3 across = normalize(cross(view, vec3(0.0, 0.0, 1.0)) + 1e-5);
            vec2 ahead = normalize(view.xy + 1e-5);
            float shift = min(0.5 / (0.03 * viewDepth), 2.0);
            vec2 at = gl_FragCoord.xy / screenSize + vec2(dot(gentleNormal.xy, across.xy), dot(gentleNormal.xy, ahead)) * shift;
            // Kept on the water's side of the horizon as water.fxc keeps it: its vertex shader projects the point
            // 2000 m ahead (along the camera's look, level) on the water's plane, its pixel shader holds the read's v
            // at least 0.5 (1 + that) + 0.05 x near + 0.005 (Direct3D's v down: here at most, mirrored).
            vec2 ahead2 = normalize(viewForward.xy + 1e-5);
            vec4 far = viewProjection * vec4(eyeAt.xy + ahead2 * 2000.0, vPosition.z, 1.0);
            if (far.w > 0.0) at.y = min(at.y, 0.5 + 0.5 * far.y / far.w - 0.05 * max(1.0 - dot(eyeAt - vPosition, eyeAt - vPosition) * 0.0004, 0.0) - 0.005);
            vec4 scene = texture(reflectTex, clamp(at, 0.001, 0.999));
            if (reflectLight) mirroredScene = scene;
            else mirrored = mirrored * (1.0 - scene.a) + scene.rgb;
          }
          // The sun's glint as water.fxc's: (saturate(R.l) + 1e-4)^512 x the sun x its shadow x gDirectionalLight.w
          // x 10 (its 10 - 9 x the shore's factor^0.25, that 0 away from the very shore line).
          // Its shadow where water.fxc reads it: at the point a metre up, that metre x (1 - 0.0004 d²) (its r0.z).
          float shadowNear = max(1.0 - dot(eyeAt - lifted, eyeAt - lifted) * 0.0004, 0.0);
          float waterShadow = outdoor ? gameSunShadow(vec3(lifted.xy, (lifted.z + waterZ) * shadowNear - waterZ)) : 1.0;
          vec3 glint = sunColor * pow(clamp(dot(bounce, sunDir), 0.0, 1.0) + 1e-4, 512.0) * waterShadow * pedShineScale.x * (10.0 - 9.0 * pow(nearField, 0.25));
          // As water.fxc, in light (as the game adds it up in its HDR buffer): its own colour (timecyc's x
          // SkyLightMult, its bottomSkyColour.w; sunny before noon black) x 0.5, the reflection times waterReflectionScale with the glint, half of it by
          // Fresnel, and a twentieth of the sun's light by the light's slope, saturate((n.l - 0.25) x 4/3);
          // then into the picture as everything else (gameToneMap). The mirrored colour is already in the
          // picture: back to light first (gameToneUnmap).
          // (With the mirrored scene's light as the game's HDR ReflectTexture: taken as it is, unclipped.)
          vec3 mirroredLight = reflectLight && reflectOn ? gameToneUnmap(mirrored) * (1.0 - clamp(mirroredScene.a, 0.0, 1.0)) + mirroredScene.rgb : gameToneUnmap(mirrored);
          // Water higher up than the sea (its vertex shader's o1.w = saturate((z - 1 - waterRenderSimParam.z) x 0.5),
          // that z 0 in the traces) mirrors the far fog's colour instead (globalFogColor; here the haze's).
          float upHigh = clamp((vPosition.z + waterZ - 1.0) * 0.5, 0.0, 1.0);
          vec3 mirroredWater = mix(mirroredLight * waterReflect, gameToneUnmap(hazeColor), upHigh);
          vec3 waterLight = waterColor * 0.5 + 0.5 * fresnel * (mirroredWater + glint)
                          + sunColor * clamp((dot(mirrorNormal, sunDir) - 0.25) * 1.3333333, 0.0, 1.0) * waterShadow * 0.05;
          // The near field's foam over it (r0.z): 3 x (the sun's light x f.x + the light from around x f.y), by the
          // mirror's normal, f = the simulation's height and speed at p x 0.05 x (0.25 x weight + 0.25) + 0.25.
          if (waterWaves && nearField > 0.0) {
            vec2 f = texture(waterSim, vPosition.xy * 0.05).xy * (0.25 * nearWeight + 0.25) + 0.25;
            vec3 foamSun = sunColor * clamp((dot(mirrorNormal, sunDir) - 0.25) * 1.3333333, 0.0, 1.0) * waterShadow;
            vec3 foamAround = mix(ambientAbove, ambientBelow, clamp(0.5 - 0.5 * mirrorNormal.z, 0.0, 1.0));
            waterLight = mix(waterLight, 3.0 * (foamSun * f.x + foamAround * f.y), nearField);
          }
          lightOut = gameFog(waterLight, distance);
          rgb = gameToneMap(lightOut);
          a = 1.0;
          // See-through by how much water lies in front of what is behind it, as water.fxc's alpha (the GTA IV trace
          // GTAIV.1's water, PS 0x24641060, blended SRCALPHA/INVSRCALPHA over the lit scene): d the view depth of
          // the scene behind less the water's (where the scene is nearer, 1), saturate((d x waterColour.w x
          // 0.25)^0.25 + 0.125); its reflection and colour then show by that much, the sea bed by the rest (bc_3's
          // shallows by the shore pale where they lay dark here).
          if (waterBehindOn) a = clamp(pow(behindDepth * waterAlpha * 0.25, 0.25) + 0.125, 0.0, 1.0);
  `,
});

  // The game's water simulation (GTAIV.exe sets it up at 0xAD61C0 and runs it each frame at 0xADC280; the
  // step is waterTex.fxc's pixel shader): a 1024 x 1024 texture tiled over the world, its red the water's height,
  // green its speed (both 0.5 at rest), blue and alpha its slope across and along (0.5 flat), stepped on each
  // frame into a second one and back. Each point is pulled towards its eight neighbours (the corners by
  // 0.707), a little back to rest, and kicked where a noise texture (random bytes, shifted at random each
  // frame) is below 0.2 in blue, by its red; then speed and height on by Verlet. The game's numbers:
  // waterSimParam = 1, 0.1, 0.01, 0.5, 0.5, 0.5, 1, the time step (the frame's milliseconds x 0.02); the kick
  // (wind x 0.75 + 0.25) x 0.1, the wind the weather's (not known here: WaterWind). 8 bits a channel, as the
  // game's render targets.
  const SIM_VERTEX = `#version 300 es
    in vec2 corner; out vec2 uv;
    void main() { uv = corner * 0.5 + 0.5; gl_Position = vec4(corner, 0.0, 1.0); }`;
  const SIM_FRAGMENT = `#version 300 es
    precision highp float;
    in vec2 uv; out vec4 outColor;
    uniform sampler2D state; uniform sampler2D noise;
    uniform vec2 noiseShift; uniform float kick; uniform float step;
    const float Texel = 1.0 / 1024.0, Threshold = 0.2;
    const float Pull = 1.0, Rest = 0.1, Accel = 0.5, Speed = 0.5, Kick = 0.5;
    float h(vec2 at) { return texture(state, at).x; }
    void main() {
      vec4 n = texture(noise, uv + noiseShift);
      float disturb = n.z - Threshold >= 0.0 ? 0.0 : (n.x * 2.0 - 1.0) * kick * Kick;
      float left = h(uv + vec2(-Texel, 0.0)), right = h(uv + vec2(Texel, 0.0));
      float down = h(uv + vec2(0.0, -Texel)), up = h(uv + vec2(0.0, Texel));
      float corners = h(uv + vec2(-Texel, -Texel)) + h(uv + vec2(Texel, -Texel)) + h(uv + vec2(-Texel, Texel)) + h(uv + vec2(Texel, Texel));
      float around = (left + right + down + up + corners * 0.707106769) * 0.146446615;
      vec4 c = texture(state, uv);
      float a = ((around - c.x) * Pull + (0.5 - c.x) * Rest + disturb) * Accel;
      float speed = c.y + a * step;
      float height = c.x + (c.y - 0.5) * Speed * step + a * step * step * 0.5;
      outColor = vec4(height, speed, (right - left) * 0.5 + 0.5, (up - down) * 0.5 + 0.5);
    }`;
  // How windy (0 to 1) the water's simulation takes it. The game's (0x12DDEB4, a byte / 255) is set only from a
  // command list filled at run time (replay or network, it seems: in single player likely 0); a breeze, as the user
  // chose, unless the mission script forces the wind ("water": bernie3's FORCE_WIND(0.0f) before bc_3, whose quad in
  // the GTA IV trace GTAIV.1 carries wind 0, its alpha 6 / 255).
  let WaterWind = 0.25;
  // The wind the sea's swell takes (see dynamicWaterStep), and the point its window keeps round: where the player
  // stands (GTAIV.exe 0x93F090), in a mission's first cutscene the mission's marker ("water": swellAt, the page's
  // metres); without one the camera.
  let SwellWind = 0, swellAt = null;
  function setWater(m) {
    if (typeof m.wind === "number") { WaterWind = m.wind; SwellWind = m.wind; }
    swellAt = Array.isArray(m.swellAt) ? m.swellAt : null;
  }
  let waterSim = null;
  // The simulation's size: GTAIV.exe 0xAD61C0 makes it 128 << the water's quality setting (0x1160EB4); the GTA IV
  // trace GTAIV.1 steps a 1024 x 1024 one (call 74263369: render target and noise 1024, waterTexResValue 0.25 x
  // 1/256, so its neighbours one texel of 1024 apart).
  const WaterSimSize = 1024;
  // The game's simulation has run since the game started; here it starts flat, so it is run on first as the game's
  // would have been at 60 a second (16 ms a step) until it has settled: its heights' and slopes' spread no longer
  // grow by then (measured here: 0.134 / 0.090 after 15 000 steps, the same after 20 000).
  const WaterSimWarmUp = 20000;
  // Run on with a stronger kick than the cutscene's: settled from its own (0.044) it stays at 0.13 / 0.09, the game's
  // in the GTA IV trace GTAIV.1 is 0.164 / 0.115 (bc_3 s1 f79, 2.6 s into the cutscene). Its step is the same as
  // SparkIV's to the bit (call 74263369 recomputed from its state and noise: all of its heights and speeds), its own kick
  // there 6 / 255 (the quad's vertex colour, wind 0), far below a step of its 8 bits; from its state the same step falls
  // to 0.13 within 2000 steps. So the game's water was driven harder before the cutscene (by what is not found: more
  // than its wind can give, at most 0.1); with 0.25 the settled water is 0.163 / 0.109 2.6 s on.
  const WaterSimWarmUpKick = 0.25;
  function waterSimStep() {
    const Size = WaterSimSize;
    if (!waterSim) {
      const simProgram = createProgram(SIM_VERTEX, SIM_FRAGMENT);
      const where = ["state", "noise", "noiseShift", "kick", "step"].reduce((o, n) => (o[n] = gl.getUniformLocation(simProgram, n), o), {});
      const quad = gl.createVertexArray();
      gl.bindVertexArray(quad);
      const buffer = gl.createBuffer();
      gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
      const corner = gl.getAttribLocation(simProgram, "corner");
      gl.enableVertexAttribArray(corner);
      gl.vertexAttribPointer(corner, 2, gl.FLOAT, false, 0, 0);
      gl.bindVertexArray(null);
      const rest = new Uint8Array(Size * Size * 4).fill(128);
      const textures = [0, 1].map(() => {
        const t = gl.createTexture();
        gl.bindTexture(gl.TEXTURE_2D, t);
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, Size, Size, 0, gl.RGBA, gl.UNSIGNED_BYTE, rest);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
        // Read without smaller levels, as the game's water reads it (the GTA IV trace GTAIV.1, bc_3, the water draw
        // 74265526: its SurfaceTextureSampler 1024 x 1024 with this one level, MINFILTER LINEAR): far off each pixel
        // takes the slope at its own point, so the mirror there breaks up into specks (the rocks' and the pier's in
        // bc_3), not averaged smooth.
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
        return t;
      });
      const frames = textures.map(t => {
        const f = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, f);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0);
        return f;
      });
      gl.bindFramebuffer(gl.FRAMEBUFFER, sceneFramebuffer);
      const noise = gl.createTexture();
      const bytes = new Uint8Array(Size * Size * 4);
      for (let i = 0; i < bytes.length; i++) bytes[i] = Math.floor(Math.random() * 256);
      gl.bindTexture(gl.TEXTURE_2D, noise);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, Size, Size, 0, gl.RGBA, gl.UNSIGNED_BYTE, bytes);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
      // The step reads its texture point by point, the water's drawing smoothly (a sampler object for the step).
      const points = gl.createSampler();
      gl.samplerParameteri(points, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
      gl.samplerParameteri(points, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
      gl.samplerParameteri(points, gl.TEXTURE_WRAP_S, gl.REPEAT);
      gl.samplerParameteri(points, gl.TEXTURE_WRAP_T, gl.REPEAT);
      waterSim = { simProgram, where, quad, textures, frames, noise, points, current: 0, last: 0 };
      for (let i = 0; i < WaterSimWarmUp; i++) waterSimRun(16, false, WaterSimWarmUpKick);
    }
    const now = performance.now();
    // The frame's milliseconds x 0.02 (the first frame, and after a pause, as at 30 a second).
    const ms = waterSim.last && now - waterSim.last < 200 ? now - waterSim.last : 33;
    waterSim.last = now;
    waterSimRun(ms, true);
  }

  // One step of the water's simulation, ms the frame's milliseconds; its mipmaps made only when it is drawn next.
  function waterSimRun(ms, shown, kick = (WaterWind * 0.75 + 0.25) * 0.1) {
    const Size = WaterSimSize;
    const next = 1 - waterSim.current;
    gl.bindFramebuffer(gl.FRAMEBUFFER, waterSim.frames[next]);
    gl.viewport(0, 0, Size, Size);
    gl.disable(gl.DEPTH_TEST); gl.disable(gl.BLEND);
    gl.useProgram(waterSim.simProgram);
    gl.activeTexture(gl.TEXTURE10); gl.bindTexture(gl.TEXTURE_2D, waterSim.textures[waterSim.current]); gl.bindSampler(10, waterSim.points);
    gl.activeTexture(gl.TEXTURE11); gl.bindTexture(gl.TEXTURE_2D, waterSim.noise);
    gl.uniform1i(waterSim.where.state, 10); gl.uniform1i(waterSim.where.noise, 11);
    gl.uniform2f(waterSim.where.noiseShift, Math.random(), Math.random());
    gl.uniform1f(waterSim.where.kick, kick);
    gl.uniform1f(waterSim.where.step, Math.round(ms) * 0.02);
    gl.bindVertexArray(waterSim.quad);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindSampler(10, null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, sceneFramebuffer);
    gl.activeTexture(gl.TEXTURE10); gl.bindTexture(gl.TEXTURE_2D, waterSim.textures[next]);
    gl.activeTexture(gl.TEXTURE0);
    gl.enable(gl.DEPTH_TEST);
    waterSim.current = next;
  }

  // The game's sea (CWater::UpdateDynamicWaterTask, GTAIV.exe 0xADBA40, set up at 0xADB770; heights read at
  // 0xAD5560): a grid of 100 x 100 points 2 m apart round the player (swellAt; else the camera; its window snapped to even metres,
  // following it; the points stay where they are in the world, those coming into the window start flat),
  // each with a height and a speed. Each frame (dt its seconds, at most 0.13): 2000 kicks at random points,
  // (random -1..1) x (wind x 0.7 + 0.23) x 10 x 0.5 x dt; a long swell pushed through it row by row,
  // sin(y x 0.15 + (time in ms % 4000) x 2pi / 4000) x (wind x 0.55 + 0.18) x dt (42 m long, one every 4 s);
  // every point pulled towards its neighbours (0.17 each side, 0.08 each corner) by 40 x dt and back to
  // rest by 0.5 x dt, damped by 0.7 ^ dt; then heights on by speed x dt. The water's slope near the camera
  // comes from it (water.fxc within 50 m: the vertex normal, from the heights 2 m to either side), faded
  // out over its window's last 20 m. The heights go to the page as a texture tiled every 200 m.
  const DynamicSize = 100, DynamicSpacing = 2;
  // How far from the camera floating litter is there (see drawMeshes): GTAIV.exe 0xC87760, 0.5 x the detail
  // distance + 20 m, the detail distance (0x103F714, set by 0xAEBD70 from 0x59EEF0) its menu slider / 99 x 100 + 10;
  // the reference videos' slider at 100: 75 m.
  const FloatingRadius = 75;

  // Whether a point lies in a vehicle of the cast as it is now (with a margin of 30 cm): in its root bone's
  // space, inside the box of its meshes shown. A vehicle: an actor with a no-water plane (boats) or of
  // vehicle materials.
  function insideVehicle(point) {
    for (const [id, a] of cast) {
      if (!a.world) continue;
      if (a.box === undefined) {
        a.box = null;
        for (const [meshId, mesh] of meshes) {
          const mt = materials.get(mesh.material);
          if (mesh.actor !== id || !visible.has(meshId) || !mt || !(mt.vehicle || mt.noWater)) continue;
          a.box = a.box || { min: [Infinity, Infinity, Infinity], max: [-Infinity, -Infinity, -Infinity] };
          for (let k = 0; k < 3; k++) { a.box.min[k] = Math.min(a.box.min[k], mesh.min[k]); a.box.max[k] = Math.max(a.box.max[k], mesh.max[k]); }
        }
      }
      if (!a.box) continue;
      const w = a.world[0], d = [point[0] - w[12], point[1] - w[13], point[2] - w[14]];
      // Into its space: the rotation's transpose (its columns are its axes).
      const local = [0, 1, 2].map(c => d[0] * w[c * 4] + d[1] * w[c * 4 + 1] + d[2] * w[c * 4 + 2]);
      if ([0, 1, 2].every(k => local[k] >= a.box.min[k] - 0.3 && local[k] <= a.box.max[k] + 0.3)) return true;
    }
    return false;
  }
  let dynamicWater = null;
  function dynamicWaterStep(eye) {
    const eyeAt = swellAt || eye;
    const n = DynamicSize;
    if (!dynamicWater) {
      const texture = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.R16F, n, n, 0, gl.RED, gl.FLOAT, new Float32Array(n * n));
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.REPEAT);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      dynamicWater = { height: new Float32Array(n * n), speed: new Float32Array(n * n), texture, x0: null, y0: null, last: 0 };
    }
    const d = dynamicWater, h = d.height, v = d.speed;
    const now = performance.now();
    const dt = Math.min(d.last ? (now - d.last) / 1000 : 1 / 30, 0.13);
    const started = !d.last;
    d.last = now;
    // Index of a point by its world metres (even): (x / 2 + 100000) % 100, as the game.
    const index = c => ((Math.trunc(c / 2) % n) + n) % n;
    const evenDown = c => { const t = Math.trunc(c) - 100; return t - (t % 2); };
    const x0 = evenDown(eyeAt[0]), y0 = evenDown(eyeAt[1]);
    // Started anew (made, or the camera jumped): run on first for as long as the cutscene has played, as the game's
    // grid started flat when the cutscene's camera came (it moved more than its 200 m window: 0xAD4E80 clears it).
    // In the GTA IV trace GTAIV.1 (bc_3 s1 f79, 2.6 s in; the water's grid round the camera, draws 74265526 to
    // 74267101) the heights' spread is 0.10 m, the vertex normals' 0.024 across and 0.065 along y; 2.6 s on from flat
    // here 0.093, 0.009, 0.068 (settled 0.13, 0.012, 0.08). Without a cutscene's time 20 s. Flat, the near water's
    // Fresnel came out a tenth too high.
    const played = typeof playback === "object" && playback && clip ? Math.min(Math.max(playback.time, 0), 30) : 20;
    const fromFlat = Math.round(played / 0.02);
    let warmUp = started ? fromFlat : 0;
    if (d.x0 === null || Math.abs(x0 - d.x0) > 200 || Math.abs(y0 - d.y0) > 200) {
      h.fill(0); v.fill(0);
      warmUp = fromFlat;
    } else {
      // The columns and rows that left the window (and come in at its other side) start flat.
      for (let x = Math.min(d.x0, x0); x < Math.max(d.x0, x0); x += 2) { const i = index(x); for (let j = 0; j < n; j++) { h[i * n + j] = 0; v[i * n + j] = 0; } }
      for (let y = Math.min(d.y0, y0); y < Math.max(d.y0, y0); y += 2) { const j = index(y); for (let i = 0; i < n; i++) { h[i * n + j] = 0; v[i * n + j] = 0; } }
    }
    d.x0 = x0; d.y0 = y0;
    const ci = index(x0), cj = index(y0);
    // SwellWind: 0 without the breeze WaterWind gives the waves' texture: settled, the trace's grid (heights' spread
    // 0.10 m, the normals' 0.024 across, 0.065 along y) lies nearer to it with no wind (0.13, 0.015, 0.090) than with
    // 0.25 (0.23, 0.027, 0.159), and bc_3's water with it (visual run, the water's references: sum -1.74).
    const kick = (SwellWind * 0.7 + 0.23) * 10, swell = (SwellWind * 0.55 + 0.18) * 1.0;
    const advance = (dt, ms) => {
      for (let k = 0; k < 2000; k++) {
        const at = Math.floor(Math.random() * n) * n + Math.floor(Math.random() * n);
        v[at] += (Math.random() * 2 - 1) * kick * 0.5 * dt;
      }
      const phase = (ms % 4000) * (2 * Math.PI / 4000);
      for (let r = 0; r < n; r++) {
        const push = Math.sin((y0 + r * DynamicSpacing) * 0.15 + phase) * swell * dt;
        const j = (cj + r) % n;
        for (let i = 0; i < n; i++) v[i * n + j] += push;
      }
      const pull = 40 * dt, rest = 0.5 * dt, damp = Math.pow(0.7, dt);
      for (let a = 0; a < n; a++) {
        const i = (ci + a) % n, iBack = a === 0 ? i : (i + n - 1) % n, iOn = a === n - 1 ? i : (i + 1) % n;
        for (let b = 0; b < n; b++) {
          const j = (cj + b) % n, jBack = b === 0 ? j : (j + n - 1) % n, jOn = b === n - 1 ? j : (j + 1) % n;
          const here = i * n + j;
          const sides = h[iBack * n + j] + h[iOn * n + j] + h[i * n + jBack] + h[i * n + jOn];
          const corners = h[iOn * n + jBack] + h[iOn * n + jOn] + h[iBack * n + jBack] + h[iBack * n + jOn];
          v[here] = ((v[here] - (h[here] - (sides * 0.17 + corners * 0.08)) * pull) - h[here] * rest) * damp;
        }
      }
      for (let k = 0; k < n * n; k++) h[k] += v[k] * dt;
    };
    // (In steps of 20 ms, the trace's frames: 16 to 23 ms. The kicks come per frame, so how high the swell settles
    // grows with the root of the step: at 1/30 s it settled 1.3 x the trace's.)
    for (let s = warmUp; s > 0; s--) advance(0.02, now - s * 20);
    advance(dt, now);
    // Into the texture: its column i, row j (x along, y up), so x / 200 and y / 200 find them.
    const rows = new Float32Array(n * n);
    for (let i = 0; i < n; i++) for (let j = 0; j < n; j++) rows[j * n + i] = h[i * n + j];
    gl.activeTexture(gl.TEXTURE12);
    gl.bindTexture(gl.TEXTURE_2D, d.texture);
    gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, n, n, gl.RED, gl.FLOAT, rows);
    gl.activeTexture(gl.TEXTURE0);
  }

  // The grid the sea near the camera is drawn as (the game's, 0xAD9130: its water quads cut into points 2 m
  // apart within the window round the camera, each lifted by the sea's height there): 101 x 101 points over
  // the 200 m window, made once (moved and lifted in the vertex shader).
  let seaGrid = null;
  function seaGridVao() {
    if (seaGrid) return seaGrid;
    const n = DynamicSize, points = new Float32Array((n + 1) * (n + 1) * 3), indices = new Uint16Array(n * n * 6);
    for (let i = 0; i <= n; i++) for (let j = 0; j <= n; j++) points.set([i * DynamicSpacing, j * DynamicSpacing, 0], (j * (n + 1) + i) * 3);
    let k = 0;
    for (let i = 0; i < n; i++) for (let j = 0; j < n; j++) {
      const a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d = c + 1;
      indices.set([a, b, c, b, d, c], k); k += 6;
    }
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, points, gl.STATIC_DRAW);
    gl.enableVertexAttribArray(loc.position);
    gl.vertexAttribPointer(loc.position, 3, gl.FLOAT, false, 0, 0);
    const elements = gl.createBuffer();
    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, elements);
    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, indices, gl.STATIC_DRAW);
    gl.bindVertexArray(null);
    seaGrid = { vao, count: indices.length };
    return seaGrid;
  }

  // Where the water is within the sea's window: the water's quads drawn from above into a texture
  // (512 x 512 over the 200 m), again whenever the window moves. The grid shows there, the quads don't.
  let waterMask = null;
  function drawWaterMask() {
    const Size = 512;
    if (!waterMask) {
      const texture = gl.createTexture();
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.R8, Size, Size, 0, gl.RED, gl.UNSIGNED_BYTE, null);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      const frame = gl.createFramebuffer();
      gl.bindFramebuffer(gl.FRAMEBUFFER, frame);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
      waterMask = { texture, frame, x0: null, y0: null };
    }
    if (waterMask.x0 === dynamicWater.x0 && waterMask.y0 === dynamicWater.y0) return;
    waterMask.x0 = dynamicWater.x0; waterMask.y0 = dynamicWater.y0;
    gl.activeTexture(gl.TEXTURE13); gl.bindTexture(gl.TEXTURE_2D, null); gl.activeTexture(gl.TEXTURE0);
    gl.bindFramebuffer(gl.FRAMEBUFFER, waterMask.frame);
    gl.viewport(0, 0, Size, Size);
    gl.clearColor(0, 0, 0, 0);
    gl.clear(gl.COLOR_BUFFER_BIT);
    gl.disable(gl.DEPTH_TEST); gl.disable(gl.CULL_FACE); gl.disable(gl.BLEND);
    gl.uniform1i(loc.maskPass, true); gl.uniform1i(loc.floating, false);
    gl.uniform1i(loc.skinned, false);
    gl.uniform1i(loc.waterGrid, false);
    for (const id of visible) {
      const m = meshes.get(id);
      const mat = m && materials.get(m.material);
      if (!mat || !mat.water) continue;
      gl.bindVertexArray(m.vao);
      gl.drawElements(gl.TRIANGLES, m.count, gl.UNSIGNED_SHORT, 0);
    }
    gl.uniform1i(loc.maskPass, false);
    gl.bindFramebuffer(gl.FRAMEBUFFER, sceneFramebuffer);
    gl.enable(gl.DEPTH_TEST);
  }

// A location's lamp (its model's light) as GTAIV.exe lights it outside by day (0xC1E200 → 0xABCCD0 →
// deferred_lighting.fxc): colour × strength in the same units as the sun (both times the time cycle's exposure),
// out to its radius; by day only with flag 0x20 (0x40 alone: a night light, not made at all; 0xC1F9A0 with the
// night factor 0 from 07:00 to 19:00). Null when it isn't on then or reaches nothing.
function outdoorLamp(l) {
  if ((l.flags & 0x20) === 0 || !(l.radius > 0.001) || !(l.strength >= 0.001)) return null;
  const exposure = sky.exposure || 1;
  return { ...l, range: l.radius, color: l.rgb.map(c => c * l.strength * exposure) };
}

// The game makes an object's lights only within 35 m of the camera (0xC1DB60).
const LampReach = 35;
// The eye's adaptation, as rage_postfx.fxc's programs 3 to 6 make it: the picture's mean luminance in light
// (programs 3 to 5: the arithmetic mean of the luminance of the game's HDR picture, x Exposure - here undoing
// the tone mapping of the drawn picture: its luminance is L^gamma x ColorCorrect x 2, the light's L / scale), then
// the adapted luminance moved towards it by min(seconds / sqrt(2 x AdaptTime), 1) (program 6; at once below
// AdaptTime 1) and kept within AdaptedLumMin and AdaptedLumMax. The picture: MidGrey / the adapted luminance.
let toneAdapted = null, toneScaleUsed = 1, toneMeasuredAt = 0, tonePixels = null;

// The tone mapping's uniforms for the frame about to be drawn.
function setToneUniforms() {
  const t = (sky && sky.tone) || {};
  const adapted = toneAdapted ?? (t.adaptedLumMin || 1);
  toneScaleUsed = (t.midGrey ?? 1) / adapted;
  gl.uniform1f(loc.toneScale, toneScaleUsed);
  gl.uniform1f(loc.toneSaturation, t.saturation ?? 1);
  gl.uniform1f(loc.toneBloom, (t.bloom ?? 0) * 0.25);
  gl.uniform3fv(loc.toneCorrect, t.colorCorrect || [0.5, 0.5, 0.5]);
  const shift = t.colorShift || [0, 0, 0];
  gl.uniform4f(loc.toneShift, shift[0], shift[1], shift[2], 1 / (shift[0] * 0.2125 + shift[1] * 0.7154 + shift[2] * 0.0721 + 0.001));
  gl.uniform1f(loc.dayNight, sky.dayNight ?? 1);
  gl.uniform2f(loc.pedShineScale, sky.directionalLightW ?? 1, sky.globalScalarsW ?? 0);
  const fog = sky.fog || {};
  gl.uniform3fv(loc.fogNear, (fog.nearColor || [0, 0, 0]).map(c => c * (sky.exposure || 1)));
  gl.uniform3f(loc.fogParams, fog.start ?? 150, fog.end ?? 1500, fog.nearAmount ?? 0);
  gl.uniform4fv(loc.depthFx, fog.depthFx || [1, 1, 0, 0]);
  gl.uniform1f(loc.depthFxExposure, sky.exposure || 1);
}

// After a frame is drawn (still in the canvas's buffer): measures it and adapts. While playing at most five
// times a second (the adaptation takes seconds); stopped, at once - true then if the picture should be drawn again.
function adaptTone(w, h, playing) {
  const t = (sky && sky.tone) || {};
  const now = performance.now();
  if (playing && toneAdapted !== null && now - toneMeasuredAt < 200) return false;
  if (!tonePixels || tonePixels.length !== w * h * 4) tonePixels = new Uint8Array(w * h * 4);
  gl.readPixels(0, 0, w, h, gl.RGBA, gl.UNSIGNED_BYTE, tonePixels);
  const cc = (t.colorCorrect || [0.5, 0.5, 0.5]).map(c => Math.max(c * 2, 1e-4));
  const gamma = sky.gamma || 1;
  let sum = 0, count = 0;
  for (let y = 0; y < h; y += 4) {
    for (let x = 0; x < w; x += 4) {
      const i = (y * w + x) * 4, a = tonePixels[i + 3];
      if (a === 0) continue;
      const lg = (tonePixels[i] / cc[0] * 0.2125 + tonePixels[i + 1] / cc[1] * 0.7154 + tonePixels[i + 2] / cc[2] * 0.0721) / a;
      // (the game measures the light without its bloom)
      sum += Math.pow(Math.max(lg, 1e-5), 1 / gamma) / (toneScaleUsed * (1 + (t.bloom ?? 0) * 0.25));
      count++;
    }
  }
  if (!count) return false;
  const current = sum / count;
  let adapted = current;
  if (playing && toneAdapted !== null && (t.adaptTime ?? 0) >= 1) {
    adapted = toneAdapted + (current - toneAdapted) * Math.min((now - toneMeasuredAt) / 1000 / Math.sqrt(2 * t.adaptTime), 1);
  }
  adapted = Math.min(Math.max(adapted, t.adaptedLumMin ?? 0), t.adaptedLumMax ?? Infinity);
  const changed = toneAdapted === null || Math.abs(adapted - toneAdapted) > toneAdapted * 0.01;
  toneAdapted = adapted;
  toneMeasuredAt = now;
  return changed && !playing;
}
// The game's post effects outside, as rage_postfx.fxc makes them of the light (the scene drawn into
// sceneFramebuffer: the picture, and the light where outside surfaces are; the sky's light taken back from its
// picture): a quarter-size copy (each texel the mean of 4 x 4, nothing that isn't a finite number: program 7),
// measured for the eye's adaptation (programs 3 to 6: the mean luminance), blurred across and down (programs 8
// and 9: each texel plus 1/5.8 of what its neighbours 1, 2 and 3 texels off are brighter by, x 0.9, 0.8, 0.7),
// then the picture (program 12): x = (light + bloom x ToneMapParams.z x 0.25) x MidGrey / adapted luminance, its
// luminance L, lerp(L, x, desaturation), the colour shift, x ColorCorrect x 2, x saturate(L)^(gamma - 1).
let sceneFramebuffer = null;
let gamePost = null;

// The sun's shadow cascades for the view vp (eye, forward, right, up, fov, aspect) and the sun's direction sun
// (towards it), as the TLAD trace GTAIV.4 shows the game's (frame 9350): across the sun, u is the camera's forward
// laid flat onto the plane square to the sun, v = u x sun; four cascades over the view's depth from its near to
// 9.5, 20, 50 and 175 m (gFacetCentre), each the box round its slice of the view on u and v (measured there 10 x
// 14, 13 x 29, 37 x 73 and 147 x 254 m: the slices' corners give the same), drawn into its quarter of the map; the
// stored heights each lower than the sun pass's by its bias (the cascades' gShadowMatrix fourth constants against
// the sun pass's: 0.14, 0.24, 0.5, 0.75 m).
const ShadowSplits = [0.1, 9.5, 20, 50, 175];
const ShadowBias = [0.14, 0.24, 0.5, 0.75];
function sunShadowCascades(vp, sun) {
  const dot3 = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
  const unit = a => { const l = Math.hypot(a[0], a[1], a[2]) || 1; return [a[0] / l, a[1] / l, a[2] / l]; };
  const h = unit(sun);
  const f = vp.forward;
  let u = unit([f[0] - h[0] * dot3(f, h), f[1] - h[1] * dot3(f, h), f[2] - h[2] * dot3(f, h)]);
  if (Math.abs(dot3(f, h)) > 0.999) u = unit([1 - h[0] * h[0], -h[0] * h[1], -h[0] * h[2]]);
  const v = [u[1] * h[2] - u[2] * h[1], u[2] * h[0] - u[0] * h[2], u[0] * h[1] - u[1] * h[0]];
  const tanV = Math.tan(vp.fov / 2), tanH = tanV * vp.aspect;
  const boxes = [];
  for (let k = 0; k < 4; k++) {
    let u0 = Infinity, u1 = -Infinity, v0 = Infinity, v1 = -Infinity;
    for (const d of [ShadowSplits[k], ShadowSplits[k + 1]]) for (const a of [-1, 1]) for (const b of [-1, 1]) {
      const p = [0, 1, 2].map(i => vp.eye[i] + (f[i] + vp.right[i] * a * tanH + vp.up[i] * b * tanV) * d);
      const pu = dot3(p, u), pv = dot3(p, v);
      u0 = Math.min(u0, pu); u1 = Math.max(u1, pu); v0 = Math.min(v0, pv); v1 = Math.max(v1, pv);
    }
    boxes.push([(u0 + u1) / 2, (v0 + v1) / 2, 2 / Math.max(u1 - u0, 1e-3), 2 / Math.max(v1 - v0, 1e-3)]);
  }
  return { u, v, h, ref: dot3(vp.eye, h), boxes };
}

// The sun's shadow map as the game makes it (GTA IV's trace GTAIV.1, bc_3; TLAD's GTAIV.2, et01_ba): each cascade
// drawn at 4096 x 4096 into R32F - cleared to -100 (cascadeConvert ps 1), what is solid without its texture
// (shadowZDir: the height along the sun, gShadowMatrix's fourth column, camera-relative), then the see-through,
// decal and cut-out materials by their own wd_draw, blended SRCALPHA / INVSRCALPHA by texture alpha x
// globalScalars.x (nothing cut: the engine's alpha test passes everything, fxc.md) - and averaged 2 x 2 into the
// 2048 x 2048 R16F map the sun pass reads (cascadeConvert ps 2). Here the map's objects kept per cascade at 4096
// (R32F, drawn again only when the cascades change), the cast drawn each frame into a work target at 4096, and each
// frame per cascade the mean of max(map, cast) over 2 x 2 into the four cascades side by side (8192 x 2048 R16F).
// Null where the browser can't draw into floats.
let shadowMapState = null;
function shadowTarget() {
  if (shadowMapState !== null) return shadowMapState || null;
  if (!gl.getExtension("EXT_color_buffer_float") || gl.getParameter(gl.MAX_TEXTURE_SIZE) < 8192) { shadowMapState = false; return null; }
  // (Blending into R32F needs it; without, the see-through ones are drawn whole.)
  const blend = !!gl.getExtension("EXT_float_blend");
  const size = 2048, big = 4096;
  const texture2D = (format, w, h) => {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texStorage2D(gl.TEXTURE_2D, 1, format, w, h);
    for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, k, v);
    gl.bindTexture(gl.TEXTURE_2D, null);
    return t;
  };
  const frameOf = (t, depth) => {
    const f = gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, f);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, t, 0);
    if (depth) gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
    return gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE ? f : null;
  };
  const depth = gl.createRenderbuffer();
  gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
  gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, big, big);
  const work = texture2D(gl.R32F, big, big), workFrame = frameOf(work, depth);
  const statics = [0, 1, 2, 3].map(() => texture2D(gl.R32F, big, big));
  const staticFrames = statics.map(t => frameOf(t, null));
  const texture = texture2D(gl.R16F, size * 4, size), frame = frameOf(texture, null);
  gl.bindFramebuffer(gl.FRAMEBUFFER, sceneFramebuffer);
  if (!workFrame || !frame || staticFrames.some(f => !f)) { shadowMapState = false; return null; }
  // The 2 x 2 mean of max(map, cast), as cascadeConvert ps 2 (its four reads half a texel off, x 0.25).
  const p = gl.createProgram();
  for (const [type, src] of [[gl.VERTEX_SHADER, `#version 300 es
    in vec2 corner; void main() { gl_Position = vec4(corner, 0.0, 1.0); }`], [gl.FRAGMENT_SHADER, `#version 300 es
    precision highp float;
    uniform highp sampler2D still; uniform highp sampler2D moving;
    out vec4 outColor;
    void main() {
      ivec2 at = ivec2(gl_FragCoord.xy) % ${size} * 2;
      float sum = 0.0;
      for (int i = 0; i < 4; i++) {
        ivec2 t = at + ivec2(i & 1, i >> 1);
        sum += max(texelFetch(still, t, 0).r, texelFetch(moving, t, 0).r);
      }
      outColor = vec4(sum * 0.25, 0.0, 0.0, 1.0);
    }`]]) {
    const s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    gl.attachShader(p, s);
  }
  gl.bindAttribLocation(p, 0, "corner");
  gl.linkProgram(p);
  const vao = gl.createVertexArray();
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);
  shadowMapState = { texture, frame, size, big, work, workFrame, statics, staticFrames, blend, key: null,
                     convert: { p, vao, still: gl.getUniformLocation(p, "still"), cast: gl.getUniformLocation(p, "moving") } };
  return shadowMapState;
}

// Each cascade's cast drawn (drawCast(k) into the bound work target) and averaged with the kept map into the map.
function shadowConvert(state, drawCast) {
  for (let k = 0; k < 4; k++) {
    gl.bindFramebuffer(gl.FRAMEBUFFER, state.workFrame);
    gl.viewport(0, 0, state.big, state.big);
    gl.clearColor(-1e9, 0, 0, 0);
    gl.depthMask(true);
    gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
    drawCast(k);
    gl.bindFramebuffer(gl.FRAMEBUFFER, state.frame);
    gl.viewport(k * state.size, 0, state.size, state.size);
    gl.disable(gl.DEPTH_TEST); gl.disable(gl.BLEND);
    gl.useProgram(state.convert.p);
    // (On units 12 and 13, given back as they were.)
    gl.activeTexture(gl.TEXTURE12); const was12 = gl.getParameter(gl.TEXTURE_BINDING_2D); gl.bindTexture(gl.TEXTURE_2D, state.statics[k]);
    gl.activeTexture(gl.TEXTURE13); const was13 = gl.getParameter(gl.TEXTURE_BINDING_2D); gl.bindTexture(gl.TEXTURE_2D, state.work);
    gl.uniform1i(state.convert.still, 12); gl.uniform1i(state.convert.cast, 13);
    gl.bindVertexArray(state.convert.vao);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindVertexArray(null);
    gl.activeTexture(gl.TEXTURE12); gl.bindTexture(gl.TEXTURE_2D, was12);
    gl.activeTexture(gl.TEXTURE13); gl.bindTexture(gl.TEXTURE_2D, was13);
    gl.activeTexture(gl.TEXTURE0);
    gl.enable(gl.DEPTH_TEST);
    gl.useProgram(program);
  }
}

// The mirrored surroundings' map as the game keeps it (the TLAD trace GTAIV.4 and bc_3's GTAIV.1): 1024 x 1024
// A16B16G16R16F with three smaller levels (512, 256, 128), each drawn from the one above by a pass of its own (its
// shader not derived: here the mipmaps), and a depth; null where the browser can't draw into half floats. The game
// binds it as ParabSampler (the sun pass) and EnvironmentSampler (vehglass).
// The lamps' shadow maps as the game keeps them (the TLAD trace GTAIV.3, draw 102287887's PS_RESOURCE_14: a 4096 x 4096
// G16R16F atlas of 1024 x 1024 a lamp, red its half on the axis's negative side, green the positive; the ceiling
// lamp in Elizabeta's lounge holding only the peds there, 1 where nothing): here LampShadowSlots lamps one above the
// other, each lamp's halves side by side at 1024, R32F, with a depth; null where the browser can't draw into floats.
let lampShadowState = null;
function lampShadowTarget() {
  if (lampShadowState !== null) return lampShadowState || null;
  const size = 1024;
  if (!gl.getExtension("EXT_color_buffer_float") || gl.getParameter(gl.MAX_TEXTURE_SIZE) < size * LampShadowSlots) { lampShadowState = false; return null; }
  const texture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, texture);
  gl.texStorage2D(gl.TEXTURE_2D, 1, gl.R32F, size * 2, size * LampShadowSlots);
  for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, k, v);
  gl.bindTexture(gl.TEXTURE_2D, null);
  const depth = gl.createRenderbuffer();
  gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
  gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, size * 2, size * LampShadowSlots);
  const frame = gl.createFramebuffer();
  gl.bindFramebuffer(gl.FRAMEBUFFER, frame);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
  gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
  const complete = gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE;
  gl.bindFramebuffer(gl.FRAMEBUFFER, sceneFramebuffer);
  if (!complete) { lampShadowState = false; return null; }
  lampShadowState = { texture, frame, size };
  return lampShadowState;
}

let parabolicMapState = null;
function parabolicTarget() {
  if (parabolicMapState !== null) return parabolicMapState || null;
  if (!gl.getExtension("EXT_color_buffer_float")) { parabolicMapState = false; return null; }
  const size = 1024;
  const texture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, texture);
  gl.texStorage2D(gl.TEXTURE_2D, 4, gl.RGBA16F, size, size);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAX_LEVEL, 3);
  gl.bindTexture(gl.TEXTURE_2D, null);
  const depth = gl.createRenderbuffer();
  gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
  gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, size, size);
  const frame = gl.createFramebuffer();
  gl.bindFramebuffer(gl.FRAMEBUFFER, frame);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
  gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
  const complete = gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE;
  gl.bindFramebuffer(gl.FRAMEBUFFER, sceneFramebuffer);
  if (!complete) { parabolicMapState = false; return null; }
  parabolicMapState = { texture, frame, size };
  return parabolicMapState;
}

function compile(type, src) { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) console.error(gl.getShaderInfoLog(s)); return s; }

function gamePostShader(fragment) {
  const vs = `#version 300 es
    in vec2 corner; out vec2 uv;
    void main() { uv = corner * 0.5 + 0.5; gl_Position = vec4(corner, 0.0, 1.0); }`;
  const common = `#version 300 es
    precision highp float;
    in vec2 uv; out vec4 outColor;
    uniform sampler2D picture; uniform sampler2D light; uniform sampler2D bloom;
    uniform vec2 texel; uniform vec2 direction;
    uniform float toneScale; uniform float toneSaturation; uniform vec3 toneCorrect; uniform vec4 toneShift;
    uniform float toneGamma; uniform float bloomScale; uniform float brightPass;
    const vec3 Luma = vec3(0.2125, 0.7154, 0.0721);
    vec3 unmap(vec3 p) {
      vec3 m = p / max(toneCorrect * 2.0, vec3(1e-4));
      float L = pow(max(dot(m, Luma), 1e-5), 1.0 / toneGamma);
      vec3 c = m / pow(clamp(L, 1e-5, 1.0), toneGamma - 1.0);
      // (the picture's colours, the sky's above all, matched to the game's own, its bloom in them: taken out)
      return max((c - (1.0 - toneSaturation) * L) / max(toneSaturation, 1e-3), vec3(0.0)) / (toneScale * (1.0 + bloomScale));
    }
    // The light at a point: the outside surfaces' own, else (the sky) back from the picture.
    vec3 lightAt(vec2 at) {
      vec4 l = texture(light, at);
      vec3 p = texture(picture, at).rgb;
      vec3 v = l.rgb + (1.0 - clamp(l.a, 0.0, 1.0)) * unmap(p);
      return all(lessThan(abs(v), vec3(65000.0))) ? v : vec3(0.0);
    }
  ` + fragment;
  const p = gl.createProgram();
  gl.attachShader(p, compile(gl.VERTEX_SHADER, vs));
  gl.attachShader(p, compile(gl.FRAGMENT_SHADER, common));
  gl.bindAttribLocation(p, 0, "corner");
  gl.linkProgram(p);
  const u = {};
  for (const n of ["picture", "light", "bloom", "depth", "dof", "focus", "noise", "grain", "texel", "direction", "toneScale", "toneSaturation", "toneCorrect", "toneShift", "toneGamma", "bloomScale", "brightPass"]) u[n] = gl.getUniformLocation(p, n);
  return { p, u };
}

function gamePostTarget(w, h, format, type, filter) {
  const texture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, texture);
  gl.texImage2D(gl.TEXTURE_2D, 0, format, w, h, 0, gl.RGBA, type, null);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, filter);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, filter);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
  return texture;
}

// Before a frame outside: the scene drawn into sceneFramebuffer (false where the browser can't: then the picture
// as before, the bloom only by the light in place, see gameToneMap).
function gamePostBegin(w, h) {
  if (gamePost === false) return false;
  if (!gamePost) {
    if (!gl.getExtension("EXT_color_buffer_float")) { gamePost = false; return false; }
    const vao = gl.createVertexArray();
    gl.bindVertexArray(vao);
    const buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    gl.enableVertexAttribArray(0);
    gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
    gl.bindVertexArray(null);
    gamePost = {
      vao, w: 0, h: 0,
      down: gamePostShader(`void main() {
        vec2 t = texel;
        vec3 sum = lightAt(uv + vec2(-t.x, -t.y)) + lightAt(uv + vec2(t.x, -t.y)) + lightAt(uv + vec2(-t.x, t.y)) + lightAt(uv + vec2(t.x, t.y));
        outColor = vec4(sum * 0.25, 1.0);
      }`),
      blur: gamePostShader(`void main() {
        vec3 c = texture(light, uv).rgb;
        vec3 sum = vec3(0.0);
        const float w[3] = float[3](0.9, 0.8, 0.7);
        for (int i = 1; i <= 3; i++) {
          vec3 a = texture(light, uv + direction * texel * float(i)).rgb - c;
          vec3 b = texture(light, uv - direction * texel * float(i)).rgb - c;
          sum += (max(a, vec3(0.0)) + max(b, vec3(0.0))) * w[i - 1];
        }
        outColor = vec4(c + sum * 0.172413796, 1.0);
      }`),
      composite: gamePostShader(`
      uniform highp sampler2D depth; uniform vec2 dof; uniform vec2 focus;
      uniform sampler2D noise; uniform vec3 grain;
      void main() {
        vec3 hdr = lightAt(uv);
        if (dof.y > 0.0) {
          // The depth of field (rage_postfx's last pass, GTAIV.1 PS 0x23e5e6a0 / TLAD's GTAIV.3 PS 0x25819460,
          // before its tone mapping): the distance from the depth; 0 between the camera's DOF near and far, rising
          // to 1 half a metre nearer than near and half a metre beyond far (dofDist (0.5, far - near, 0.5,
          // (near + far) / 2)), squared; four filtered reads around (half and one and a half texels off, turned),
          // and 1 also where the point stands out from them (its luminance further from their mean than they
          // spread); then lerp(point, their mean). Recomputed per pixel from the inputs of that pass against the
          // game's picture (GTAIV.1 call 74970168, GTAIV.3 call 120953415, near 5.9 m there): within rounding.
          float d = texture(depth, uv).r;
          float z = 2.0 * dof.x * dof.y / (dof.y + dof.x - (d * 2.0 - 1.0) * (dof.y - dof.x));
          // x DOF_BLUR's x and z: misc.DOFBlurMultiplier of visualSettings.dat, its HD one (1): read with x32dbg at
          // 0x930580 during a cutscene at the reference videos' settings (1600 x 900, 23.10.: DOF_BLUR (1, 0, 1, 0));
          // an earlier read at other settings gave the SD one (0.5). All four traces have (1, 0, 1, 0).
          float b = min(max(max(focus.x - z, z - focus.y), 0.0) * 2.0, 1.0) * 1.0;
          b *= b;
          // The game's offsets are in Direct3D's texture space (v down the screen), here up: y the other way.
          vec3 s0 = lightAt(uv + texel * vec2(-0.5, 1.5)), s1 = lightAt(uv + texel * vec2(1.5, 0.5));
          vec3 s2 = lightAt(uv + texel * vec2(0.5, -1.5)), s3 = lightAt(uv + texel * vec2(-1.5, -0.5));
          vec4 l = vec4(dot(s0, Luma), dot(s1, Luma), dot(s2, Luma), dot(s3, Luma));
          float mean = dot(l, vec4(0.25));
          vec4 off = l - mean;
          float centre = dot(hdr, Luma) - mean;
          if (centre * centre - dot(off, off) >= 0.0) b = 1.0;
          // The point itself, where the G-buffer has GB2.w (A2R10G10B10: 0, 1/3, 2/3, 1): halfway to the read at
          // (-1.5, 0.5) (the pass picks one of the four by GB2.w x 256 in (0, 2], (2, 4], (4, 8], (8, 256]: with
          // two bits always the last). The G-buffer is cleared to 0 (GTAIV.1 call 74953082) and every program
          // that fills it writes its alpha there (gta_default, gta_normal_spec: alpha x globalScalars.x, kept only
          // from 0.176 on, so at least 1/3; gta_ped: 1): so everything drawn, not the sky. With it the recomputed
          // pictures match the game's (99.8 % of GTAIV.1's within one step, 79 % without).
          if (d < 1.0) hdr = 0.5 * hdr + 0.5 * s3;
          hdr = hdr * (1.0 - b) + (s0 + s1 + s2 + s3) * (b * 0.25);
        }
        // The film grain (TLAD; rage_postfx.fxc programs 25 and 27, chosen while the time cycle's grain isn't 0:
        // GTAIV.exe 0x9301D7): a 1024 x 1024 texture of random bytes the game makes at its start, read at
        // frac(uv x (3.2, 1.8) + NoiseParams.xy) - xy random each frame (0x93020F) -, its blue - 0.5, x
        // NoiseParams.z x 1.1, onto the light after the depth of field (here x Exposure: the page's light is the
        // game's x Exposure). grain: NoiseParams.x, .y, .z x Exposure.
        if (grain.z > 0.0) hdr += (texture(noise, fract(uv * vec2(3.2, 1.8) + grain.xy)).b - 0.5) * grain.z * 1.1;
        // The bloom only above the bright pass (program 12: max(bloom x Exposure - ToneMapParams.x x adapted /
        // MidGrey, 0) x ToneMapParams.z x 0.25, then all x MidGrey / adapted; here the light is x Exposure
        // already, toneScale MidGrey / adapted): em_4's bloom 10 in TLAD's Elizabeta flat (et01_aa) stays on the
        // lamps instead of lighting the whole room.
        vec3 x = hdr * toneScale + max(texture(bloom, uv).rgb * toneScale - brightPass, 0.0) * bloomScale;
        float L = dot(x, Luma);
        vec3 c = mix(vec3(L), x, toneSaturation);
        float s = L * toneShift.w;
        c = clamp(s, 0.0, 1.0) * (c - toneShift.rgb * s) + toneShift.rgb * s;
        c *= toneCorrect * 2.0;
        outColor = vec4(clamp(c * pow(clamp(L, 1e-5, 1.0), toneGamma - 1.0), 0.0, 1.0), 1.0);
      }`),
    };
  }
  const post = gamePost;
  if (post.w !== w || post.h !== h) {
    for (const t of [post.picture, post.light, post.depth, post.solidDepth, post.quarter, post.blurred]) if (t) gl.deleteTexture(t);
    for (const r of [post.msPicture, post.msLight, post.msDepth]) if (r) gl.deleteRenderbuffer(r);
    const samples = Math.min(4, gl.getParameter(gl.MAX_SAMPLES));
    const rb = format => { const r = gl.createRenderbuffer(); gl.bindRenderbuffer(gl.RENDERBUFFER, r); gl.renderbufferStorageMultisample(gl.RENDERBUFFER, samples, format, w, h); return r; };
    post.msPicture = rb(gl.RGBA8); post.msLight = rb(gl.RGBA16F); post.msDepth = rb(gl.DEPTH24_STENCIL8);
    post.ms = post.ms || gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, post.ms);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.RENDERBUFFER, post.msPicture);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT1, gl.RENDERBUFFER, post.msLight);
    gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_STENCIL_ATTACHMENT, gl.RENDERBUFFER, post.msDepth);
    post.picture = gamePostTarget(w, h, gl.RGBA8, gl.UNSIGNED_BYTE, gl.LINEAR);
    post.light = gamePostTarget(w, h, gl.RGBA16F, gl.HALF_FLOAT, gl.LINEAR);
    post.resolve = post.resolve || gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, post.resolve);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, post.picture, 0);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT1, gl.TEXTURE_2D, post.light, 0);
    // The depth too (for the depth of field), read point by point.
    post.depth = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, post.depth);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.DEPTH24_STENCIL8, w, h, 0, gl.DEPTH_STENCIL, gl.UNSIGNED_INT_24_8, null);
    for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, k, v);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_STENCIL_ATTACHMENT, gl.TEXTURE_2D, post.depth, 0);
    // The depth of the solid scene alone, as it stands before the water and the glass (see gameCoronas).
    post.solidDepth = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, post.solidDepth);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.DEPTH24_STENCIL8, w, h, 0, gl.DEPTH_STENCIL, gl.UNSIGNED_INT_24_8, null);
    for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, k, v);
    post.solidFrame = post.solidFrame || gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, post.solidFrame);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.DEPTH_STENCIL_ATTACHMENT, gl.TEXTURE_2D, post.solidDepth, 0);
    post.qw = Math.max(1, w >> 2); post.qh = Math.max(1, h >> 2);
    post.quarter = gamePostTarget(post.qw, post.qh, gl.RGBA16F, gl.HALF_FLOAT, gl.LINEAR);
    post.blurred = gamePostTarget(post.qw, post.qh, gl.RGBA16F, gl.HALF_FLOAT, gl.LINEAR);
    post.quarterFrame = post.quarterFrame || gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, post.quarterFrame);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, post.quarter, 0);
    post.blurFrame = post.blurFrame || gl.createFramebuffer();
    gl.bindFramebuffer(gl.FRAMEBUFFER, post.blurFrame);
    gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, post.blurred, 0);
    post.w = w; post.h = h;
  }
  sceneFramebuffer = post.ms;
  gl.bindFramebuffer(gl.FRAMEBUFFER, post.ms);
  gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1]);
  return true;
}

// Only the picture drawn to (the sky, markers: no light of their own), or both again.
function gamePostLight(on) {
  if (sceneFramebuffer) gl.drawBuffers(on ? [gl.COLOR_ATTACHMENT0, gl.COLOR_ATTACHMENT1] : [gl.COLOR_ATTACHMENT0, gl.NONE]);
}

function gamePostUniforms(u) {
  const t = (sky && sky.tone) || {};
  const shift = t.colorShift || [0, 0, 0];
  gl.uniform1f(u.toneScale, (t.midGrey ?? 1) / (toneAdapted ?? (t.adaptedLumMin || 1)));
  gl.uniform1f(u.toneSaturation, t.saturation ?? 1);
  gl.uniform3fv(u.toneCorrect, t.colorCorrect || [0.5, 0.5, 0.5]);
  gl.uniform4f(u.toneShift, shift[0], shift[1], shift[2], 1 / (shift[0] * 0.2125 + shift[1] * 0.7154 + shift[2] * 0.0721 + 0.001));
  gl.uniform1f(u.toneGamma, sky.gamma || 1);
  gl.uniform1f(u.bloomScale, (t.bloom ?? 0) * 0.25);
  gl.uniform1f(u.brightPass, t.brightPass ?? 0);
  gl.uniform1i(u.picture, 0); gl.uniform1i(u.light, 1); gl.uniform1i(u.bloom, 2); gl.uniform1i(u.depth, 3); gl.uniform1i(u.noise, 4);
}

// The film grain's texture as the game makes it (the TLAD trace: a 1024 x 1024 A8R8G8B8 texture filled with random
// bytes from memory at the start; read filtered, clamped), once.
let grainTexture = null;
function gameGrainTexture() {
  if (grainTexture) return grainTexture;
  const size = 1024, bytes = new Uint8Array(size * size * 4);
  for (let i = 0; i < bytes.length; i += 65536) crypto.getRandomValues(bytes.subarray(i, i + 65536));
  grainTexture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, grainTexture);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA8, size, size, 0, gl.RGBA, gl.UNSIGNED_BYTE, bytes);
  for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.LINEAR], [gl.TEXTURE_MAG_FILTER, gl.LINEAR], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, k, v);
  return grainTexture;
}

// After the frame: the post effects into the canvas. True when stopped and the adaptation moved (drawn again).
// depthOfField: through a cutscene's camera { near, far: the depth's range, focusNear, focusFar: the camera's DOF },
// else null. GTAIV.exe: Cut_StartSection (0x9C9180) puts its post effects into the cutscene mode (+0x264), in which
// FUN_005E16F0 makes DOF_PARAMS (0.5, far - near, 0.5, (near + far) / 2) from the camera clip's CameraDof
// (near, far; measured with x32dbg) and the end of 0x930A80 DOF_BLUR (M, M x 0, M, 0), M the
// misc.DOFBlurMultiplier of visualSettings.dat (its HD one, 1, at the reference videos' settings: x32dbg, 23.10.):
// sharp from near to far, from half a metre nearer or beyond fully blurred (the traces' last pass, see the
// composite). Outside cutscenes its fourth value (0x931560: the toggle at 0x11A28F0, 1 in the game recorded)
// turns it off, and with it the G-buffer's softening (GB2.w). The pass's motion blur (gDirectionalMotionBlurLength)
// is 0 in all four traces, so it isn't here.
// volumes: the lamps' light volumes to add (see gameLightVolumes), or null; coronas: the lamps' coronas (see
// gameCoronas), or null.
function gamePostEnd(w, h, playing, depthOfField, volumes, coronas) {
  const post = gamePost;
  sceneFramebuffer = null;
  gl.bindFramebuffer(gl.READ_FRAMEBUFFER, post.ms);
  gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, post.resolve);
  gl.readBuffer(gl.COLOR_ATTACHMENT0); gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.NONE]);
  gl.blitFramebuffer(0, 0, w, h, 0, 0, w, h, gl.COLOR_BUFFER_BIT, gl.NEAREST);
  gl.readBuffer(gl.COLOR_ATTACHMENT1); gl.drawBuffers([gl.NONE, gl.COLOR_ATTACHMENT1]);
  gl.blitFramebuffer(0, 0, w, h, 0, 0, w, h, gl.COLOR_BUFFER_BIT, gl.NEAREST);
  if (depthOfField || volumes) gl.blitFramebuffer(0, 0, w, h, 0, 0, w, h, gl.DEPTH_BUFFER_BIT, gl.NEAREST);
  gl.disable(gl.DEPTH_TEST); gl.disable(gl.BLEND); gl.disable(gl.STENCIL_TEST); gl.disable(gl.CULL_FACE);
  if (volumes) gameLightVolumes(post, w, h, volumes);
  if (coronas) gameCoronas(post, w, h, coronas);
  gl.bindVertexArray(post.vao);
  const pass = (shader, frame, vw, vh, textures, set) => {
    gl.bindFramebuffer(gl.FRAMEBUFFER, frame);
    gl.viewport(0, 0, vw, vh);
    gl.useProgram(shader.p);
    gamePostUniforms(shader.u);
    textures.forEach((t, i) => { gl.activeTexture(gl.TEXTURE0 + i); gl.bindTexture(gl.TEXTURE_2D, t); });
    if (set) set(shader.u);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
  };
  // The quarter copy (each texel the mean of the 4 x 4 below it: four filtered reads between them).
  pass(post.down, post.quarterFrame, post.qw, post.qh, [post.picture, post.light], u => gl.uniform2f(u.texel, 1 / w, 1 / h));
  const changed = gamePostAdapt(post, playing);
  pass(post.blur, post.blurFrame, post.qw, post.qh, [null, post.quarter], u => { gl.uniform2f(u.texel, 1 / post.qw, 1 / post.qh); gl.uniform2f(u.direction, 1, 0); });
  pass(post.blur, post.quarterFrame, post.qw, post.qh, [null, post.blurred], u => { gl.uniform2f(u.texel, 1 / post.qw, 1 / post.qh); gl.uniform2f(u.direction, 0, 1); });
  const grain = ((sky && sky.tone) || {}).noise || 0;
  pass(post.composite, null, w, h, [post.picture, post.light, post.quarter, post.depth, grain > 0 ? gameGrainTexture() : null], u => {
    gl.uniform2f(u.texel, 1 / w, 1 / h);
    const d = depthOfField;
    gl.uniform2f(u.dof, d ? d.near : 0, d ? d.far : 0);
    gl.uniform2f(u.focus, d ? d.focusNear : 0, d ? d.focusFar : 0);
    gl.uniform3f(u.grain, Math.random(), Math.random(), grain * (sky.exposure || 1));
  });
  for (let i = 0; i < 5; i++) { gl.activeTexture(gl.TEXTURE0 + i); gl.bindTexture(gl.TEXTURE_2D, null); }
  gl.activeTexture(gl.TEXTURE0);
  gl.bindVertexArray(null);
  gl.enable(gl.DEPTH_TEST);
  gl.useProgram(program);
  return changed;
}

// Lamps' light volumes (2dfx flag 0x80000: a cone of light in the air), added to the light after the scene as the
// game's deferred_lighting draws them (GTAIV.exe 0xAC2270; TLAD trace GTAIV.3 frame 13868, et01_aa's spot, draws
// 120953037 and 120953053, PS 0x25d425e0 / 0x25d418c0, ONE / ONE, no depth test):
// - its shape: the game's own unit mesh (made at its start, see gameLightVolumeMesh), mapped as deferred_lighting's
//   vs07 maps it: a point lamp's normalised; a spot's xy round its axis (x along its tangent, y along tangent x
//   direction), z -1 on the axis to 0 at the outer angle: the angle (1 + z) x the outer angle, across x sin x c213,
//   along x cos; xy 0 and z 0 the lamp itself. All x c213 x radius x 0.662, the radius the lamp's x the volume's size;
// - per pixel, along the ray from the eye to the shape's face, cut where the scene's depth is nearer: the part of it
//   within a sphere of 0.66 x that radius round the lamp (s from the ray's point nearest the lamp, half-chord c),
//   f = (clamp(s at the face) - clamp(s at the eye))² / (3 x (0.66 r)²), each clamped to -c..c;
// - lightShafts' passes 4 and 5 (programs vs07 + ps20 / ps21): the first culls CCW and adds f, the second culls CW
//   and takes f off - in the trace +0.833 then -0.092 at (912,253) (state dumps at calls 120953036, 038, 054), as f
//   at the far and the near face of the game's mesh come out there (0.093 near, recomputed): the far faces add, the
//   near ones take off. Here by the faces' winding: drawn with WebGL's default (CCW front), the game's strips show
//   their near faces as front faces (taking f off), the far ones as back faces (adding it);
// - x the lamp's colour x the volume's strength (its own x the lamp's Range) x 0.25; here x Exposure (the page's
//   light is the game's x Exposure).
// That for lamps with flag 0x100000; those without it take passes 0 and 1 (ps18 / ps19), the same mesh and passes
// with another f: the lookup texture (lights_occluders.wtd's "volume") at the angle the eye and the point make
// seen from the lamp (see the fragment program) - recomputed at four pixels of the bc_1 trace GTAIV.2, draw 1539854,
// to 0.0001.
// volumes: { list: [{ at, dir, tangent, rgb, radius, strength, cone (outer angle in degrees, 0 a point lamp),
// lookup (without flag 0x100000) }], lookup (the texture), matrix, eye, near, far, exposure }.

// The light volumes' unit meshes as GTAIV.exe makes them at its start (0xABE3A0): a spot's by 0xAC1C00 with n 5, the
// lower half of an octahedron in steps of 1/n (its four quarters, z < 0) and a fan from the middle to its rim at z 0;
// a point lamp's by 0xAC0B20 with n 4, the whole octahedron (its eight eighths). Triangle strips (degenerate
// triangles between the rows); the spot's 256 vertices equal those the game filled its buffer with (TLAD trace
// GTAIV.3, call 287065). Its c213 (gDeferredVolumeRadiusScale) from n as 0xABE3A0 makes it: a = (π/2) / n,
// 1 / (((2 cos a + 1) / 3)² + 2 (sin a / 3)²) - 1.044903 and 1.071094 as in all four traces.
function gameLightVolumeMesh(n, eighths, fan) {
  const out = [];
  for (let b = 0; b < eighths; b++) {
    const lx = b & 1 ? 1 : -1, ly = b & 2 ? 1 : -1, z = b & 4 ? 1 : -1;
    let u = lx === ly ? 1 : 0;
    if (z < 0) u = 1 - u;
    const A = u === 1 ? [lx, 0, 0] : [0, ly, 0], B = u === 1 ? [0, ly, 0] : [lx, 0, 0];
    const side = (P, a) => [P[0] - P[0] * a, P[1] - P[1] * a, P[2] + (z - P[2]) * a];
    const lerp = (p, q, t) => [p[0] + (q[0] - p[0]) * t, p[1] + (q[1] - p[1]) * t, p[2] + (q[2] - p[2]) * t];
    for (let k = 0; k < n; k++) {
      const m = n - k, P0 = side(A, k / n), Q0 = side(B, k / n), P1 = side(A, (k + 1) / n), Q1 = side(B, (k + 1) / n);
      if (fan && k === 0) for (let j = 0; j < m; j++) {
        if (j === 0) out.push(0, 0, 0);
        out.push(0, 0, 0, ...lerp(P0, Q0, j / m));
        if (j === m - 1) out.push(...Q0, ...Q0, ...Q0);
      }
      for (let j = 0; j < m; j++) {
        const p = lerp(P0, Q0, j / m);
        if (j === 0) out.push(...p);
        out.push(...p, ...lerp(P1, Q1, m > 1 ? j / (m - 1) : j / m));
        if (j === m - 1) out.push(...Q0, ...Q0, ...Q0);
      }
    }
  }
  const a = Math.PI / 2 / n, c = (2 * Math.cos(a) + 1) / 3, s = Math.sin(a) / 3;
  return { data: out, scale: 1 / (c * c + 2 * s * s) };
}

let lightVolume = null;
function gameLightVolumes(post, w, h, volumes) {
  if (!lightVolume) {
    const vs = `#version 300 es
      precision highp float;
      in vec3 unit;
      uniform mat4 viewProjection; uniform vec3 eye; uniform vec3 lampAt; uniform vec3 lampDir; uniform vec3 lampAcross;
      uniform float cone; uniform float scale; uniform float size;
      out vec3 ray; out float w;
      void main() {
        vec3 d;
        if (cone <= 0.0) d = normalize(unit + 1e-5);
        else {
          float xy = dot(unit.xy, unit.xy);
          if (xy >= 1e-4) {
            float a = cone * (1.0 - clamp(-unit.z, 0.0, 1.0));
            float c = cos(a);
            float k = sqrt((1.0 - c * c) / (xy + 1e-4)) * scale;
            d = cross(lampAcross, lampDir) * (k * unit.y) + lampAcross * (k * unit.x) + lampDir * c;
          } else d = abs(unit.z) >= 1e-4 ? lampDir : vec3(0.0);
        }
        vec3 world = lampAt + d * size;
        ray = world - eye;
        gl_Position = viewProjection * vec4(world, 1.0);
        w = gl_Position.w;
      }`;
    const fs = `#version 300 es
      precision highp float;
      in vec3 ray; in float w; out vec4 outColor;
      uniform sampler2D depth; uniform vec2 range; uniform vec3 eyeFromLamp; uniform float radius; uniform vec3 color;
      uniform bool lookup; uniform sampler2D volumeLookup;
      void main() {
        float d = texelFetch(depth, ivec2(gl_FragCoord.xy), 0).r * 2.0 - 1.0;
        float sceneZ = 2.0 * range.x * range.y / (range.y + range.x - d * (range.y - range.x));
        float t = clamp(sceneZ / w, 0.0, 1.0);
        float len = length(ray);
        vec3 dir = ray / max(len, 1e-5);
        float sEye = dot(dir, eyeFromLamp);
        float h = length(eyeFromLamp - dir * sEye);
        float f;
        if (lookup) {
          // ps18 / ps19: the eye and the point (face or scene) seen from the lamp, scaled by sqrt(K / (1 + K h²)),
          // K = min(16 / radius², 16); the angle between them looked up (u 0.125, v 0.5 + 0.5 cos), times π /
          // (1.5 sqrt(1 + K h²)).
          float k = min(16.0 / (radius * radius), 16.0);
          float s = sqrt(1.0 + k * h * h);
          vec3 p = (eyeFromLamp + ray * t) * (sqrt(k) / s), e = eyeFromLamp * (sqrt(k) / s);
          float c = dot(p, e) * inversesqrt(dot(e, e) + 1e-5) * inversesqrt(dot(p, p) + 1e-5);
          f = texture(volumeLookup, vec2(0.125, c * 0.5 + 0.5)).r * 3.14159274 / (1.5 * s);
        } else {
          // ps20 / ps21.
          float sFace = len * t + sEye;
          float r = 0.66 * radius;
          float c = h < r ? sqrt(r * r - h * h) : 0.0;
          float x = clamp(sFace, -c, c) - clamp(sEye, -c, c);
          f = x * x / (3.0 * r * r);
        }
        outColor = vec4(color * f * (gl_FrontFacing ? -1.0 : 1.0), 0.0);
      }`;
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl.VERTEX_SHADER, vs));
    gl.attachShader(p, compile(gl.FRAGMENT_SHADER, fs));
    gl.bindAttribLocation(p, 0, "unit");
    gl.linkProgram(p);
    const u = {};
    for (const n of ["viewProjection", "eye", "lampAt", "lampDir", "lampAcross", "cone", "scale", "size", "depth", "range", "eyeFromLamp", "radius", "color", "lookup", "volumeLookup"]) u[n] = gl.getUniformLocation(p, n);
    const mesh = made => {
      const vao = gl.createVertexArray();
      gl.bindVertexArray(vao);
      gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
      gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(made.data), gl.STATIC_DRAW);
      gl.enableVertexAttribArray(0);
      gl.vertexAttribPointer(0, 3, gl.FLOAT, false, 0, 0);
      return { vao, count: made.data.length / 3, scale: made.scale };
    };
    lightVolume = { p, u, spot: mesh(gameLightVolumeMesh(5, 4, true)), point: mesh(gameLightVolumeMesh(4, 8, false)), frame: gl.createFramebuffer() };
  }
  const v = lightVolume;
  gl.bindFramebuffer(gl.FRAMEBUFFER, v.frame);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, post.light, 0);
  gl.drawBuffers([gl.COLOR_ATTACHMENT0]);
  gl.viewport(0, 0, w, h);
  gl.useProgram(v.p);
  gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, post.depth);
  gl.uniform1i(v.u.depth, 0);
  gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, volumes.lookup || null);
  gl.uniform1i(v.u.volumeLookup, 1);
  gl.activeTexture(gl.TEXTURE0);
  gl.uniformMatrix4fv(v.u.viewProjection, false, volumes.matrix);
  gl.uniform3fv(v.u.eye, volumes.eye);
  gl.uniform2f(v.u.range, volumes.near, volumes.far);
  gl.enable(gl.BLEND); gl.blendFunc(gl.ONE, gl.ONE);
  for (const l of volumes.list) {
    const m = l.cone > 0 ? v.spot : v.point;
    gl.uniform3fv(v.u.lampAt, l.at);
    gl.uniform3fv(v.u.lampDir, l.dir);
    gl.uniform3fv(v.u.lampAcross, l.tangent);
    gl.uniform3f(v.u.eyeFromLamp, volumes.eye[0] - l.at[0], volumes.eye[1] - l.at[1], volumes.eye[2] - l.at[2]);
    // The outer angle as the game hands it on (Light_Add: degrees x π/180).
    gl.uniform1f(v.u.cone, l.cone > 0 ? l.cone * Math.PI / 180 : 0);
    gl.uniform1f(v.u.scale, m.scale);
    gl.uniform1f(v.u.size, m.scale * l.radius * 0.662);
    gl.uniform1f(v.u.radius, l.radius);
    gl.uniform1i(v.u.lookup, l.lookup ? 1 : 0);
    const k = l.strength * 0.25 * volumes.exposure;
    gl.uniform3f(v.u.color, l.rgb[0] * k, l.rgb[1] * k, l.rgb[2] * k);
    gl.bindVertexArray(m.vao);
    gl.drawArrays(gl.TRIANGLE_STRIP, 0, m.count);
  }
  gl.disable(gl.BLEND);
  gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, null);
  gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, null);
  gl.bindVertexArray(post.vao);
}

// The solid scene's depth, kept before the water and the glass are drawn (for gameCoronas).
function gameKeepSolidDepth() {
  const post = gamePost;
  gl.bindFramebuffer(gl.READ_FRAMEBUFFER, post.ms);
  gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, post.solidFrame);
  gl.blitFramebuffer(0, 0, post.w, post.h, 0, 0, post.w, post.h, gl.DEPTH_BUFFER_BIT, gl.NEAREST);
  gl.bindFramebuffer(gl.FRAMEBUFFER, post.ms);
}

// Lamps' coronas, added to the light after the scene as the game's deferred_lighting draws them (technique corona;
// the bc_1 trace GTAIV.trace, f414.25: draw 114115754, ONE / ONE, no depth test, sprite "corona" of coronas.wtd,
// the depth texture 0x46363ae0 at the vertex sampler 0). That draw alone is what lay over Bernie's flat: without it
// the game's picture equals SparkIV's across the room. That depth is the solid scene's: the forward draws before it
// write to another surface (0x2cb64c80, the texture's being 0x2db36a00), so a corona shines through the flat's glass.
// Every 2dfx light hands one in (Light_FromEffect 0xC1E200 -> 0xC1F020):
// - its colour the light's bytes, its strength the light's Intensity (+0x5C), for an object outside an interior x the
//   time cycle's SprBght (0.1 x the column x 10, as 0xAEFEB0 keeps it), x the object's fading alpha (+0x63: not here,
//   in the bc_3 trace GTAIV.1 only the few far ones at their LOD's distance, 0.09 to 0.40 of 0.5); its size CoronaSize
//   (+0x40) x 5000 x 0.0005 (0xC1EE40) half a side; those of size 0 (the bc_1 trace's 11 of 16) draw nothing;
// - its place towards the camera from the lamp, in an interior 1 m (all 16 in that trace to 1 mm, whatever their
//   size), else its size (x 1 less the modifier's strength 0x103F9A4, taken as 0);
// - no limit of distance: the bc_3 trace draws them to 234 m, where the object is drawn;
// - a quad facing the camera (its right and up);
// - the vertex program (read from the trace, its VS 0x2dc06a80): nearer to the camera than its size, no depth
//   test; x sat(0.5 (f / size + 1)) for f, its distance along the view, below its size, and below 0.1 m in front moved
//   out to there. Else 12 points of the Poisson disc 2 pixels round its middle against the frame's depth, x their
//   share in front, and collapsed to a point where that is 0.01 or less;
// - the pixel: the sprite x colour x strength. Here x Exposure (the page's light is the game's x Exposure).
// coronas: { list: [{ at, rgb, size, strength }], sprite, matrix, eye, right, up, forward, exposure }.
let corona = null;
function gameCoronas(post, w, h, coronas) {
  if (!corona) {
    const vs = `#version 300 es
      precision highp float;
      uniform mat4 viewProjection; uniform vec3 eye; uniform vec3 viewRight; uniform vec3 viewUp; uniform vec3 viewForward;
      uniform vec3 at; uniform float size; uniform vec3 color; uniform vec2 pixel;
      uniform highp sampler2D depth;
      out vec2 uv; out vec3 tint;
      const vec2 Poisson[12] = vec2[12](vec2(-0.326212, -0.40581), vec2(-0.840144, -0.07358), vec2(-0.695914, 0.457137),
        vec2(-0.203345, 0.620716), vec2(0.96234, -0.194983), vec2(0.473434, -0.480026), vec2(0.519456, 0.767022),
        vec2(0.185461, -0.893124), vec2(0.507431, 0.064425), vec2(0.89642, 0.412458), vec2(-0.32194, -0.932615),
        vec2(-0.791559, -0.59771));
      const vec2 Corners[6] = vec2[6](vec2(-1, -1), vec2(1, -1), vec2(1, 1), vec2(1, 1), vec2(-1, 1), vec2(-1, -1));
      void main() {
        vec2 corner = Corners[gl_VertexID];
        vec3 place = at + (viewRight * corner.x + viewUp * corner.y) * size;
        float seen = 1.0;
        if (length(at - eye) < size) {
          float f = dot(viewForward, at - eye);
          if (f < size) {
            seen = clamp(0.5 * (f / size + 1.0), 0.0, 1.0);
            if (f < 0.1) place += viewForward * (0.1 - f);
          }
        } else {
          vec4 clip = viewProjection * vec4(at, 1.0);
          vec3 ndc = clip.xyz / clip.w;
          seen = 0.0;
          for (int i = 0; i < 12; i++) {
            vec2 p = clamp(ndc.xy + pixel * Poisson[i] * 2.0, -0.997, 0.997);
            if (ndc.z * 0.5 + 0.5 < textureLod(depth, p * 0.5 + 0.5, 0.0).r) seen += 1.0 / 12.0;
          }
          if (clip.w <= 0.0 || seen <= 0.01) { seen = 0.0; place = at; }
        }
        gl_Position = viewProjection * vec4(place, 1.0);
        // Drawn without a depth test, so its depth is free: kept off the near plane, which lies at 0.1 m for the
        // cutscene's camera (the game's at 0.05 m) - right where a corona nearer than its size is moved to. Else it
        // fell away there (bc_3 s3 f140: the pier lamp 0.93 m off, all of the game's picture +0.12 / +0.07 / +0.02).
        gl_Position.z = 0.0;
        tint = color * seen;
        uv = corner * 0.5 + 0.5;
      }`;
    const fs = `#version 300 es
      precision highp float;
      in vec2 uv; in vec3 tint; out vec4 outColor;
      uniform sampler2D sprite;
      void main() { outColor = vec4(texture(sprite, uv).rgb * tint, 0.0); }`;
    const p = gl.createProgram();
    gl.attachShader(p, compile(gl.VERTEX_SHADER, vs));
    gl.attachShader(p, compile(gl.FRAGMENT_SHADER, fs));
    gl.linkProgram(p);
    const u = {};
    for (const n of ["viewProjection", "eye", "viewRight", "viewUp", "viewForward", "at", "size", "color", "pixel", "depth", "sprite"]) u[n] = gl.getUniformLocation(p, n);
    corona = { p, u, frame: gl.createFramebuffer(), vao: gl.createVertexArray() };
  }
  const c = corona;
  gl.bindFramebuffer(gl.FRAMEBUFFER, c.frame);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, post.light, 0);
  gl.drawBuffers([gl.COLOR_ATTACHMENT0]);
  gl.viewport(0, 0, w, h);
  gl.useProgram(c.p);
  gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, post.solidDepth);
  gl.uniform1i(c.u.depth, 0);
  gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, coronas.sprite);
  gl.uniform1i(c.u.sprite, 1);
  gl.uniformMatrix4fv(c.u.viewProjection, false, coronas.matrix);
  gl.uniform3fv(c.u.eye, coronas.eye);
  gl.uniform3fv(c.u.viewRight, coronas.right);
  gl.uniform3fv(c.u.viewUp, coronas.up);
  gl.uniform3fv(c.u.viewForward, coronas.forward);
  gl.uniform2f(c.u.pixel, 1 / w, 1 / h);
  gl.enable(gl.BLEND); gl.blendFunc(gl.ONE, gl.ONE);
  gl.bindVertexArray(c.vao);
  for (const l of coronas.list) {
    gl.uniform3fv(c.u.at, l.at);
    gl.uniform1f(c.u.size, l.size);
    const k = l.strength * coronas.exposure;
    gl.uniform3f(c.u.color, l.rgb[0] * k, l.rgb[1] * k, l.rgb[2] * k);
    gl.drawArrays(gl.TRIANGLES, 0, 6);
  }
  gl.disable(gl.BLEND);
  gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, null);
  gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, null);
  gl.bindVertexArray(post.vao);
}

// The eye's adaptation from the quarter copy (before the blur): its mean luminance, moved towards as adaptTone
// says; at most five times a second while playing.
let gamePostPixels = null;
function gamePostAdapt(post, playing) {
  const t = (sky && sky.tone) || {};
  const now = performance.now();
  if (playing && toneAdapted !== null && now - toneMeasuredAt < 200) return false;
  const n = post.qw * post.qh * 4;
  if (!gamePostPixels || gamePostPixels.length !== n) gamePostPixels = new Float32Array(n);
  gl.bindFramebuffer(gl.FRAMEBUFFER, post.quarterFrame);
  gl.readPixels(0, 0, post.qw, post.qh, gl.RGBA, gl.FLOAT, gamePostPixels);
  let sum = 0;
  for (let i = 0; i < n; i += 4) sum += gamePostPixels[i] * 0.2125 + gamePostPixels[i + 1] * 0.7154 + gamePostPixels[i + 2] * 0.0721;
  const current = sum / (n / 4);
  let adapted = current;
  if (playing && toneAdapted !== null && (t.adaptTime ?? 0) >= 1) {
    adapted = toneAdapted + (current - toneAdapted) * Math.min((now - toneMeasuredAt) / 1000 / Math.sqrt(2 * t.adaptTime), 1);
  }
  adapted = Math.min(Math.max(adapted, t.adaptedLumMin ?? 0), t.adaptedLumMax ?? Infinity);
  const changed = toneAdapted === null || Math.abs(adapted - toneAdapted) > toneAdapted * 0.01;
  toneAdapted = adapted;
  toneMeasuredAt = now;
  return changed && !playing;
}