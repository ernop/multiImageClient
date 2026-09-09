"use strict";

// Browser-local, deterministic coarse segmentation for photo-derived layout
// templates. The worker sees a bounded preview only; the source file never
// leaves the browser and none of its pixels are returned in the flat map.
self.onmessage = (event) => {
  const request = event.data;
  try {
    if (!request || request.version !== 1
        || !Number.isInteger(request.width) || request.width < 2
        || !Number.isInteger(request.height) || request.height < 2
        || !Number.isInteger(request.regionCount)
        || request.regionCount < 2 || request.regionCount > 9
        || !(request.data instanceof ArrayBuffer)
        || request.data.byteLength !== request.width * request.height * 4) {
      throw new Error("segmentation request did not follow the required contract");
    }
    const rgba = new Uint8ClampedArray(request.data);
    const labels = segment(
      rgba,
      request.width,
      request.height,
      request.regionCount);
    self.postMessage({
      version: 1,
      requestId: request.requestId,
      width: request.width,
      height: request.height,
      regionCount: request.regionCount,
      labels: labels.buffer,
    }, [labels.buffer]);
  } catch (error) {
    self.postMessage({
      version: 1,
      requestId: request?.requestId,
      error: error instanceof Error ? error.message : String(error),
    });
  }
};

function segment(rgba, width, height, count) {
  const total = width * height;
  const l = new Float32Array(total);
  const a = new Float32Array(total);
  const b = new Float32Array(total);
  const luminance = new Float32Array(total);
  const gradient = new Float32Array(total);
  const texture = new Float32Array(total);

  for (let i = 0; i < total; i++) {
    const offset = i * 4;
    const lab = rgbToLab(rgba[offset], rgba[offset + 1], rgba[offset + 2]);
    l[i] = lab[0];
    a[i] = lab[1];
    b[i] = lab[2];
    luminance[i] = 0.2126 * rgba[offset] + 0.7152 * rgba[offset + 1] + 0.0722 * rgba[offset + 2];
  }
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const i = y * width + x;
      const left = luminance[y * width + Math.max(0, x - 1)];
      const right = luminance[y * width + Math.min(width - 1, x + 1)];
      const up = luminance[Math.max(0, y - 1) * width + x];
      const down = luminance[Math.min(height - 1, y + 1) * width + x];
      gradient[i] = Math.min(1, Math.hypot(right - left, down - up) / 180);
      let sum = 0;
      let sumSq = 0;
      let samples = 0;
      for (let yy = Math.max(0, y - 2); yy <= Math.min(height - 1, y + 2); yy += 2) {
        for (let xx = Math.max(0, x - 2); xx <= Math.min(width - 1, x + 2); xx += 2) {
          const value = luminance[yy * width + xx] / 255;
          sum += value;
          sumSq += value * value;
          samples++;
        }
      }
      texture[i] = Math.sqrt(Math.max(0, sumSq / samples - (sum / samples) ** 2));
    }
  }

  const centers = initialCenters(width, height, count, gradient).map((index) => ({
    l: l[index],
    a: a[index],
    b: b[index],
    x: (index % width) / Math.max(1, width - 1),
    y: Math.floor(index / width) / Math.max(1, height - 1),
    texture: texture[index],
  }));
  const labels = new Uint8Array(total);
  const distances = new Float32Array(total);

  for (let iteration = 0; iteration < 12; iteration++) {
    for (let y = 0; y < height; y++) {
      const ny = y / Math.max(1, height - 1);
      for (let x = 0; x < width; x++) {
        const nx = x / Math.max(1, width - 1);
        const i = y * width + x;
        let best = 0;
        let bestDistance = Infinity;
        for (let c = 0; c < centers.length; c++) {
          const center = centers[c];
          const dl = l[i] - center.l;
          const da = a[i] - center.a;
          const db = b[i] - center.b;
          const dx = nx - center.x;
          const dy = ny - center.y;
          const dt = texture[i] - center.texture;
          const distance =
            dl * dl * 0.7 + da * da + db * db
            + (dx * dx + dy * dy) * 1700
            + dt * dt * 12000
            + gradient[i] * 18;
          if (distance < bestDistance) {
            bestDistance = distance;
            best = c;
          }
        }
        labels[i] = best;
        distances[i] = bestDistance;
      }
    }

    const sums = Array.from({ length: count }, () => ({
      l: 0, a: 0, b: 0, x: 0, y: 0, texture: 0, n: 0,
    }));
    for (let y = 0; y < height; y++) {
      const ny = y / Math.max(1, height - 1);
      for (let x = 0; x < width; x++) {
        const i = y * width + x;
        const sum = sums[labels[i]];
        sum.l += l[i];
        sum.a += a[i];
        sum.b += b[i];
        sum.x += x / Math.max(1, width - 1);
        sum.y += ny;
        sum.texture += texture[i];
        sum.n++;
      }
    }
    for (let c = 0; c < count; c++) {
      const sum = sums[c];
      if (sum.n === 0) {
        let farthest = 0;
        for (let i = 1; i < total; i++) {
          if (distances[i] > distances[farthest]) farthest = i;
        }
        centers[c] = {
          l: l[farthest],
          a: a[farthest],
          b: b[farthest],
          x: (farthest % width) / Math.max(1, width - 1),
          y: Math.floor(farthest / width) / Math.max(1, height - 1),
          texture: texture[farthest],
        };
      } else {
        centers[c] = {
          l: sum.l / sum.n,
          a: sum.a / sum.n,
          b: sum.b / sum.n,
          x: sum.x / sum.n,
          y: sum.y / sum.n,
          texture: sum.texture / sum.n,
        };
      }
    }
  }

  // Relabel by top-to-bottom, then left-to-right centroid order so reruns have
  // stable palette identities instead of arbitrary cluster numbering.
  const order = centers
    .map((center, index) => ({ index, x: center.x, y: center.y }))
    .sort((left, right) => left.y - right.y || left.x - right.x);
  const remap = new Uint8Array(count);
  order.forEach((entry, index) => { remap[entry.index] = index; });
  for (let i = 0; i < labels.length; i++) labels[i] = remap[labels[i]];
  return labels;
}

function initialCenters(width, height, count, gradient) {
  const columns = Math.ceil(Math.sqrt(count * width / height));
  const rows = Math.ceil(count / columns);
  const result = [];
  for (let index = 0; index < count; index++) {
    const row = Math.floor(index / columns);
    const column = index % columns;
    const targetX = Math.round((column + 0.5) * width / columns);
    const targetY = Math.round((row + 0.5) * height / rows);
    let bestX = Math.max(0, Math.min(width - 1, targetX));
    let bestY = Math.max(0, Math.min(height - 1, targetY));
    let bestGradient = gradient[bestY * width + bestX];
    for (let dy = -4; dy <= 4; dy++) {
      for (let dx = -4; dx <= 4; dx++) {
        const x = Math.max(0, Math.min(width - 1, targetX + dx));
        const y = Math.max(0, Math.min(height - 1, targetY + dy));
        const value = gradient[y * width + x];
        if (value < bestGradient) {
          bestGradient = value;
          bestX = x;
          bestY = y;
        }
      }
    }
    result.push(bestY * width + bestX);
  }
  return result;
}

function rgbToLab(r, g, b) {
  const linear = (value) => {
    value /= 255;
    return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4;
  };
  r = linear(r);
  g = linear(g);
  b = linear(b);
  const x = (r * 0.4124 + g * 0.3576 + b * 0.1805) / 0.95047;
  const y = r * 0.2126 + g * 0.7152 + b * 0.0722;
  const z = (r * 0.0193 + g * 0.1192 + b * 0.9505) / 1.08883;
  const pivot = (value) => value > 0.008856 ? Math.cbrt(value) : 7.787 * value + 16 / 116;
  const fx = pivot(x);
  const fy = pivot(y);
  const fz = pivot(z);
  return [116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz)];
}
