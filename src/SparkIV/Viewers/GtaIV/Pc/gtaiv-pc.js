"use strict";
// GTA IV (PC) for the model view (ModelView.html): what the page draws as that game and version draws it, taken from
// its shaders (common\shaders\win32_30\*.fxc), timecyc.dat and GTAIV.exe. The page puts the GLSL parts (GameGlsl) into
// its shaders and calls the rest (the water simulation, the sea, the lamps outside) by name; another game or engine
// version brings its own file with the same names. SparkIV serves it before the page's own script.

const GameGlsl = Object.freeze({
  // The fragment shader's uniforms for the parts below.
  uniforms: `
    // How bright the water's reflection is (water.fxc's waterReflectionScale: the time cycle's, see TimeCycle.cs).
    uniform float waterReflect;
    // The picture's gamma (rage_postfx.fxc's deSatContrastGamma.z: the time cycle's Gamma).
    uniform float toneGamma;
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
      if (vehicle && useSpecMap && useTexture && reflectivePower > 0.0) {
        vec4 spec = texture(specTex, vUv);
        float strength = dot(spec.rgb, specMask) * 0.15 * reflectivePower * (1.0 - dirtAmount);
        float sharp = max(spec.a * 190.0 * reflectivePower, 1.0);
        vec3 l = outdoor ? sunDir : light;
        vec3 h = normalize(l + normalize(eyeAt - vPosition));
        vehicleShine = strength * pow(max(dot(n, h), 0.0), sharp) * (outdoor ? sunColor : vec3(1.0));
        // The second shine, added to the colour as gta_vehicle_paint2 writes it into the game's colour buffer
        // (then lit as the colour is): the sun mirrored in the paint, pow(dot(mirrored view, sun), factor x spec
        // alpha x reflectivity) x factor x dot(spec map, mask) x reflectivity x the colour.
        if (spec2Scale.y > 0.0) {
          vec3 mirrored = reflect(normalize(vPosition - eyeAt), n);
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
          // Fresnel, and a twentieth of the sun's light by the light's slope, saturate((n.l - 0.25) x 4/3).
          // Into the picture and back as rage_postfx.fxc's tone mapping does it: by the luminance only,
          // colour x saturate(L)^(gamma - 1), so the hue stays (the mirrored sky's pale blue, darker: grey-blue
          // as in the game, not the saturated blue a curve per channel makes of it). The mirrored colour is
          // already in the picture: back to light by L^((1 - gamma) / gamma).
          const vec3 Luma = vec3(0.2125, 0.7154, 0.0721);
          vec3 mirroredLight = mirrored * pow(max(dot(mirrored, Luma), 1e-4), (1.0 - toneGamma) / toneGamma);
          vec3 waterLight = waterColor * 0.5 + 0.5 * fresnel * (mirroredLight * waterReflect + glint)
                          + sunColor * clamp((dot(lightNormal, sunDir) - 0.25) * 1.3333333, 0.0, 1.0) * 0.05;
          rgb = min(waterLight * pow(clamp(dot(waterLight, Luma), 1e-4, 1.0), toneGamma - 1.0), 1.0);
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
      gl.bindFramebuffer(gl.FRAMEBUFFER, null);
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
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
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
  // How far from the camera floating litter is there (see drawMeshes).
  const FloatingRadius = 30;

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
    gl.bindFramebuffer(gl.FRAMEBUFFER, null);
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