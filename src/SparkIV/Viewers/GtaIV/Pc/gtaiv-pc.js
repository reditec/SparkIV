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
    // The mirrored surroundings' map (see parabolicVertex): being drawn into it, the camera's place (its centre),
    // the map and whether it is there to read (see mirroredAround).
    uniform bool parabolic; uniform vec3 parabolicAt; uniform sampler2D parabolicMap; uniform bool parabolicOn;
    // The sun's shadow (see gameSunShadow, sunShadowCascades): its map of the four cascades side by side, the
    // light's axes (u, v across, h towards the sun), each cascade's box (centre on u and v, 1 / half its size),
    // the camera's forward axis, the heights' reference (the camera's height along h); drawing into the map
    // (shadowPass) less the cascade's bias.
    uniform bool shadowOn; uniform highp sampler2D shadowMap; uniform vec3 shadowU; uniform vec3 shadowV; uniform vec3 shadowH;
    uniform vec4 shadowBox[4]; uniform vec3 viewForward; uniform float shadowRef; uniform bool shadowPass; uniform float shadowBias;
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
        float stored0 = dot(vPosition, shadowH) - shadowRef - shadowBias;
        outColor = vec4(stored0, stored0, 0.0, 1.0);
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
      float ao = clamp(baked ? dayNight * vColor.r + (1.0 - dayNight) * vColor.g : vColor.r, 0.0, 1.0) * actorAmbient;
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
      vec3 pedShine = vec3(0.0);
      if (outdoor && !vehicle && useSpecMap && useTexture) {
        vec4 spec = texture(specTex, vUv);
        float shineAmount = (baked ? 0.5 : 0.25) * dot(spec.rgb, specMask) * specIntensity + 0.5 * pedRim * pedRim;
        float power = spec.a * specPower * (baked ? 1.0 : 0.75);
        vec3 view = normalize(vPosition - eyeAt);
        vec3 mirroredDir = reflect(view, n);
        float fresnel = 0.75 * pow(1.0 - abs(dot(view, n)), 4.0) + 0.25;
        float away = length(vPosition - eyeAt);
        vec3 mapDir = normalize(mirroredDir + view * clamp(away * 0.02, 0.0, 1.0));
        vec3 skyLight = mirroredAround(mapDir, 4.0 - 4.0 * clamp(power / 230.0, 0.0, 1.0));
        float nearby = 1.0 - clamp(away * 0.01, 0.0, 1.0);
        if (glassForward) {
          // A map's glass instead by its own forward pass (gta_glass_spec program 20, the trace's booth panes from
          // call 145517152): dot(spec map, specMapIntMask) x specularColorFactor x the sun x its shadow x
          // (saturate(R.l) + 1e-4)^(spec map alpha x specularFactor + 1e-4) x gDirectionalLight.w; no mirrored
          // surroundings (glass isn't in the G-buffer the sun pass reads).
          pedShine = 2.0 * shineAmount * sunColor * sunShadow * pow(clamp(dot(mirroredDir, sunDir), 0.0, 1.0) + 1e-4, power + 1e-4) * pedShineScale.x;
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
    vec3 mirroredAround(vec3 r, float lod) {
      vec3 skyLight = gameToneUnmap(mix(skyColor, skyTopColor, clamp(r.z * 2.5, 0.0, 1.0)));
      if (!parabolicOn) return skyLight;
      vec4 m = textureLod(parabolicMap, 0.5 + r.xy / (2.0 * (1.0 + clamp(r.z, 0.0, 1.0))), lod);
      return m.rgb + (1.0 - m.a) * skyLight;
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
        vec2 stored = texture(shadowMap, uv + ShadowTaps[i] / 2048.0).rg;
        lit += up0 >= max(stored.r, stored.g) ? 1.0 : 0.0;
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
      bool vehicleGlass = vehicleSpec.z > 0.5;
      // (The glass also without a spec map, as 1: which texture the game binds to an unset SpecSampler is not
      // traced.)
      if (vehicle && (vehicleGlass || (useSpecMap && useTexture)) && reflectivePower > 0.0) {
        vec4 spec = useSpecMap && useTexture ? texture(specTex, vUv) : vec4(1.0);
        // How strong and how sharp, by the shader (vehicleSpec, from its pixel programs: paint 0.15 and 190,
        // vehicle_mesh 1 and 50, vehglass 0.225 and 190; GtaIVPcMaterials.VehicleShineOf).
        float strength = dot(spec.rgb, specMask) * vehicleSpec.x * reflectivePower;
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
          // A person's see-through part as gta_ped.fxc's forward shader: alpha test at 100 / 255, then of the
          // pixels only the share its stipple level keeps (stipple.wtd all_stips: kept of 64 per level,
          // level = alpha x 3.996 - row its whole part, column a quarter of the rest), each blended by
          // alpha. Here as that share of the alpha, without the 8 x 8 pattern.
          if (a < 100.0 / 255.0) discard;
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
          // The light takes their slopes as they are, the mirror four times steeper (the game's x and y of
          // its slopes and its z and w).
          vec3 view = normalize(vPosition - eyeAt);
          vec3 lightNormal = vec3(0.0, 0.0, 1.0);
          vec3 mirrorNormal = lightNormal;
          if (waterWaves) {
            vec2 p = vPosition.xy;
            float distance2 = dot(eyeAt - vPosition, eyeAt - vPosition);
            float near = max(1.0 - distance2 * 0.0004, 0.0);
            vec2 big = texture(waterSim, p * 0.002).zw - 0.5;
            vec2 middle = texture(waterSim, p * 0.01).zw - 0.5;
            vec2 small = (texture(waterSim, p * 0.0454545).zw - 0.5) * near;
            vec2 lightSlope = big * 0.0512 * (1.0 - near) + middle * 0.256 + small * 0.116363637;
            vec2 mirrorSlope = big * 0.2048 * (1.0 - near) + middle * 1.024 + small * 0.465454549;
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
            lightNormal = normalize(vec3(lightSlope, 1.00001));
            mirrorNormal = normalize(vec3(mirrorSlope, 1.00001));
          }
          float cosine = abs(dot(view, mirrorNormal));
          float fresnel = pow(1.0 - cosine, 5.0) * 0.6 + 0.2;
          vec3 bounce = reflect(view, mirrorNormal);
          vec3 mirrored = mix(skyColor, skyTopColor, clamp(bounce.z * 2.5, 0.0, 1.0));
          if (reflectOn) {
            // The mirrored scene where it is, shifted by the waves as water.fxc does: their slope turned to the
            // picture (across, up) times 0.5 / (0.03 x the distance), at most twice; the sky where nothing was drawn.
            vec3 across = normalize(cross(view, vec3(0.0, 0.0, 1.0)) + 1e-5);
            vec2 ahead = normalize(view.xy + 1e-5);
            float shift = min(0.5 / (0.03 * max(length(eyeAt - vPosition), 1.0)), 2.0);
            vec2 at = gl_FragCoord.xy / screenSize + vec2(dot(mirrorNormal.xy, across.xy), dot(mirrorNormal.xy, ahead)) * shift;
            vec4 scene = texture(reflectTex, clamp(at, 0.001, 0.999));
            mirrored = mirrored * (1.0 - scene.a) + scene.rgb;
          }
          vec3 glint = sunColor * pow(max(dot(bounce, sunDir), 0.0), 512.0);
          // As water.fxc, in light (as the game adds it up in its HDR buffer): its own colour (timecyc's, sunny
          // at noon black) x 0.5, the reflection times waterReflectionScale with the glint, half of it by
          // Fresnel, and a twentieth of the sun's light by the light's slope, saturate((n.l - 0.25) x 4/3);
          // then into the picture as everything else (gameToneMap). The mirrored colour is already in the
          // picture: back to light first (gameToneUnmap).
          vec3 mirroredLight = gameToneUnmap(mirrored);
          vec3 waterLight = waterColor * 0.5 + 0.5 * fresnel * (mirroredLight * waterReflect + glint)
                          + sunColor * clamp((dot(lightNormal, sunDir) - 0.25) * 1.3333333, 0.0, 1.0) * 0.05;
          lightOut = gameFog(waterLight, distance);
          rgb = gameToneMap(lightOut);
          a = 1.0;
  `,
});

  // The game's water simulation (GTAIV.exe sets it up at 0xAD61C0 and runs it each frame at 0xADC280; the
  // step is waterTex.fxc's pixel shader): a 256 x 256 texture tiled over the world, its red the water's height,
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
    const float Texel = 1.0 / 256.0, Threshold = 0.2;
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
  // command list filled at run time (replay or network, it seems: in single player likely 0); a breeze, as the user chose.
  const WaterWind = 0.25;
  let waterSim = null;
  function waterSimStep() {
    const Size = 256;
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
        gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR_MIPMAP_LINEAR);
        gl.generateMipmap(gl.TEXTURE_2D);
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
    }
    const now = performance.now();
    // The frame's milliseconds x 0.02 (the first frame, and after a pause, as at 30 a second).
    const ms = waterSim.last && now - waterSim.last < 200 ? now - waterSim.last : 33;
    waterSim.last = now;
    const next = 1 - waterSim.current;
    gl.bindFramebuffer(gl.FRAMEBUFFER, waterSim.frames[next]);
    gl.viewport(0, 0, Size, Size);
    gl.disable(gl.DEPTH_TEST); gl.disable(gl.BLEND);
    gl.useProgram(waterSim.simProgram);
    gl.activeTexture(gl.TEXTURE10); gl.bindTexture(gl.TEXTURE_2D, waterSim.textures[waterSim.current]); gl.bindSampler(10, waterSim.points);
    gl.activeTexture(gl.TEXTURE11); gl.bindTexture(gl.TEXTURE_2D, waterSim.noise);
    gl.uniform1i(waterSim.where.state, 10); gl.uniform1i(waterSim.where.noise, 11);
    gl.uniform2f(waterSim.where.noiseShift, Math.random(), Math.random());
    gl.uniform1f(waterSim.where.kick, (WaterWind * 0.75 + 0.25) * 0.1);
    gl.uniform1f(waterSim.where.step, Math.round(ms) * 0.02);
    gl.bindVertexArray(waterSim.quad);
    gl.drawArrays(gl.TRIANGLES, 0, 3);
    gl.bindSampler(10, null);
    gl.bindFramebuffer(gl.FRAMEBUFFER, sceneFramebuffer);
    gl.activeTexture(gl.TEXTURE10); gl.bindTexture(gl.TEXTURE_2D, waterSim.textures[next]);
    gl.generateMipmap(gl.TEXTURE_2D);
    gl.activeTexture(gl.TEXTURE0);
    gl.enable(gl.DEPTH_TEST);
    waterSim.current = next;
  }

  // The game's sea (CWater::UpdateDynamicWaterTask, GTAIV.exe 0xADBA40, set up at 0xADB770; heights read at
  // 0xAD5560): a grid of 100 x 100 points 2 m apart round the camera (its window snapped to even metres,
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
  function dynamicWaterStep(eyeAt) {
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
    d.last = now;
    // Index of a point by its world metres (even): (x / 2 + 100000) % 100, as the game.
    const index = c => ((Math.trunc(c / 2) % n) + n) % n;
    const evenDown = c => { const t = Math.trunc(c) - 100; return t - (t % 2); };
    const x0 = evenDown(eyeAt[0]), y0 = evenDown(eyeAt[1]);
    if (d.x0 === null || Math.abs(x0 - d.x0) > 200 || Math.abs(y0 - d.y0) > 200) {
      h.fill(0); v.fill(0);
    } else {
      // The columns and rows that left the window (and come in at its other side) start flat.
      for (let x = Math.min(d.x0, x0); x < Math.max(d.x0, x0); x += 2) { const i = index(x); for (let j = 0; j < n; j++) { h[i * n + j] = 0; v[i * n + j] = 0; } }
      for (let y = Math.min(d.y0, y0); y < Math.max(d.y0, y0); y += 2) { const j = index(y); for (let i = 0; i < n; i++) { h[i * n + j] = 0; v[i * n + j] = 0; } }
    }
    d.x0 = x0; d.y0 = y0;
    const ci = index(x0), cj = index(y0);
    const kick = (WaterWind * 0.7 + 0.23) * 10, swell = (WaterWind * 0.55 + 0.18) * 1.0;
    for (let k = 0; k < 2000; k++) {
      const at = Math.floor(Math.random() * n) * n + Math.floor(Math.random() * n);
      v[at] += (Math.random() * 2 - 1) * kick * 0.5 * dt;
    }
    const phase = (now % 4000) * (2 * Math.PI / 4000);
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

// The sun's shadow map as the game keeps it: the four cascades side by side, 2048 x 2048 each (8192 x 2048; the game
// draws each at 4096 x 4096 into R32F and averages 2 x 2 into R16F, 0x23CFA700 - here drawn at 2048 into R32F
// directly, read unfiltered), with a depth; null where the browser can't draw into floats. Two heights per texel:
// red the map's objects (still: drawn again only when the cascades change), green the cast (drawn each frame into
// a cleared green); gameSunShadow takes the higher.
let shadowMapState = null;
function shadowTarget() {
  if (shadowMapState !== null) return shadowMapState || null;
  if (!gl.getExtension("EXT_color_buffer_float") || gl.getParameter(gl.MAX_TEXTURE_SIZE) < 8192) { shadowMapState = false; return null; }
  const size = 2048;
  const texture = gl.createTexture();
  gl.bindTexture(gl.TEXTURE_2D, texture);
  gl.texStorage2D(gl.TEXTURE_2D, 1, gl.RG32F, size * 4, size);
  for (const [k, v] of [[gl.TEXTURE_MIN_FILTER, gl.NEAREST], [gl.TEXTURE_MAG_FILTER, gl.NEAREST], [gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE], [gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE]]) gl.texParameteri(gl.TEXTURE_2D, k, v);
  gl.bindTexture(gl.TEXTURE_2D, null);
  const depth = gl.createRenderbuffer();
  gl.bindRenderbuffer(gl.RENDERBUFFER, depth);
  gl.renderbufferStorage(gl.RENDERBUFFER, gl.DEPTH_COMPONENT24, size * 4, size);
  const frame = gl.createFramebuffer();
  gl.bindFramebuffer(gl.FRAMEBUFFER, frame);
  gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
  gl.framebufferRenderbuffer(gl.FRAMEBUFFER, gl.DEPTH_ATTACHMENT, gl.RENDERBUFFER, depth);
  const complete = gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE;
  gl.bindFramebuffer(gl.FRAMEBUFFER, sceneFramebuffer);
  if (!complete) { shadowMapState = false; return null; }
  shadowMapState = { texture, frame, size, key: null };
  return shadowMapState;
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
  const compile = (type, src) => { const s = gl.createShader(type); gl.shaderSource(s, src); gl.compileShader(s); if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) console.error(gl.getShaderInfoLog(s)); return s; };
  const p = gl.createProgram();
  gl.attachShader(p, compile(gl.VERTEX_SHADER, vs));
  gl.attachShader(p, compile(gl.FRAGMENT_SHADER, common));
  gl.bindAttribLocation(p, 0, "corner");
  gl.linkProgram(p);
  const u = {};
  for (const n of ["picture", "light", "bloom", "depth", "dof", "noise", "grain", "texel", "direction", "toneScale", "toneSaturation", "toneCorrect", "toneShift", "toneGamma", "bloomScale", "brightPass"]) u[n] = gl.getUniformLocation(p, n);
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
      uniform highp sampler2D depth; uniform vec4 dof;
      uniform sampler2D noise; uniform vec3 grain;
      void main() {
        vec3 hdr = lightAt(uv);
        if (dof.w > 0.0) {
          // The depth of field (program 12 before its tone mapping): the distance from the depth, in a cutscene
          // 0 nearer than its camera's far, rising to 1 half a metre beyond it, squared; four filtered reads
          // around (half and one and a half texels off, turned), and 1 also where the point stands out from
          // them (its luminance further from their mean than they spread); then lerp(point, their mean).
          float d = texture(depth, uv).r * 2.0 - 1.0;
          float z = 2.0 * dof.x * dof.y / (dof.y + dof.x - d * (dof.y - dof.x));
          // x DOF_BLUR's z: misc.DOFBlurMultiplier of visualSettings.dat, its HD one (1): read with x32dbg at
          // 0x930580 during a cutscene at the reference videos' settings (1600 x 900, 23.10.: DOF_BLUR (1, 0, 1, 0));
          // an earlier read at other settings gave the SD one (0.5).
          float b = min(max(z - dof.z, 0.0) * 2.0, 1.0) * 1.0;
          b *= b;
          vec3 s0 = lightAt(uv + texel * vec2(-0.5, -1.5)), s1 = lightAt(uv + texel * vec2(1.5, -0.5));
          vec3 s2 = lightAt(uv + texel * vec2(0.5, 1.5)), s3 = lightAt(uv + texel * vec2(-1.5, 0.5));
          vec4 l = vec4(dot(s0, Luma), dot(s1, Luma), dot(s2, Luma), dot(s3, Luma));
          float mean = dot(l, vec4(0.25));
          vec4 off = l - mean;
          float centre = dot(hdr, Luma) - mean;
          if (centre * centre - dot(off, off) >= 0.0) b = 1.0;
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
    for (const t of [post.picture, post.light, post.depth, post.quarter, post.blurred]) if (t) gl.deleteTexture(t);
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
// depthOfField: through a cutscene's camera { near, far: the depth's range, focus: the camera's DOF far }, else
// null. GTAIV.exe: Cut_StartSection (0x9C9180) puts its post effects into the cutscene mode (+0x264), in which
// FUN_005E16F0 makes DOF_PARAMS (0.5, far - near, 0.5, (near + far) / 2) from the camera clip's CameraDof
// (near, far; measured with x32dbg) and the end of 0x930A80 DOF_BLUR (M, M x 0, M, 0), M the
// misc.DOFBlurMultiplier of visualSettings.dat (its HD one, 1, at the reference videos' settings: x32dbg, 23.10.):
// sharp to far, from half a metre beyond fully blurred. Outside cutscenes its fourth value (0x931560: the toggle at 0x11A28F0, 1 in the game
// recorded) turns it off.
function gamePostEnd(w, h, playing, depthOfField) {
  const post = gamePost;
  sceneFramebuffer = null;
  gl.bindFramebuffer(gl.READ_FRAMEBUFFER, post.ms);
  gl.bindFramebuffer(gl.DRAW_FRAMEBUFFER, post.resolve);
  gl.readBuffer(gl.COLOR_ATTACHMENT0); gl.drawBuffers([gl.COLOR_ATTACHMENT0, gl.NONE]);
  gl.blitFramebuffer(0, 0, w, h, 0, 0, w, h, gl.COLOR_BUFFER_BIT, gl.NEAREST);
  gl.readBuffer(gl.COLOR_ATTACHMENT1); gl.drawBuffers([gl.NONE, gl.COLOR_ATTACHMENT1]);
  gl.blitFramebuffer(0, 0, w, h, 0, 0, w, h, gl.COLOR_BUFFER_BIT, gl.NEAREST);
  if (depthOfField) gl.blitFramebuffer(0, 0, w, h, 0, 0, w, h, gl.DEPTH_BUFFER_BIT, gl.NEAREST);
  gl.disable(gl.DEPTH_TEST); gl.disable(gl.BLEND); gl.disable(gl.STENCIL_TEST); gl.disable(gl.CULL_FACE);
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
    gl.uniform4f(u.dof, d ? d.near : 0, d ? d.far : 1, d ? d.focus : 0, d ? 1 : 0);
    gl.uniform3f(u.grain, Math.random(), Math.random(), grain * (sky.exposure || 1));
  });
  for (let i = 0; i < 5; i++) { gl.activeTexture(gl.TEXTURE0 + i); gl.bindTexture(gl.TEXTURE_2D, null); }
  gl.activeTexture(gl.TEXTURE0);
  gl.bindVertexArray(null);
  gl.enable(gl.DEPTH_TEST);
  gl.useProgram(program);
  return changed;
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