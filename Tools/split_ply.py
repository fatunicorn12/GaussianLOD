#!/usr/bin/env python3
"""
Spatially partition a Gaussian Splat PLY file into a grid of smaller PLY files.
Designed for mixed indoor/outdoor scenes where a single massive PLY exceeds
practical single-asset limits in Unity.

Usage:
    python split_ply.py \
        --input my_scene.ply \
        --output-dir ./zones \
        --grid 3x3 \
        --axis xz \
        --overlap 0.05 \
        --min-splats 50000

Dependencies: numpy (required), tqdm (optional, for progress bars).
"""

import argparse
import os
import struct
import sys
from pathlib import Path

import numpy as np

try:
    from tqdm import tqdm
except ImportError:
    tqdm = None


# ---------------------------------------------------------------------------
# PLY parsing
# ---------------------------------------------------------------------------

def parse_ply_header(f):
    """Parse a PLY header, return (format, properties, header_end_offset, vertex_count)."""
    magic = f.readline().strip()
    if magic != b"ply":
        raise ValueError("Not a PLY file (missing 'ply' magic).")

    fmt = None
    properties = []  # list of (name, dtype_str)
    vertex_count = 0
    in_vertex_element = False

    while True:
        line = f.readline()
        if not line:
            raise ValueError("Unexpected end of file in PLY header.")
        line = line.strip()
        if line == b"end_header":
            break

        parts = line.decode("ascii", errors="replace").split()
        if not parts:
            continue

        if parts[0] == "format":
            fmt = parts[1]
            if fmt not in ("binary_little_endian", "ascii"):
                raise ValueError(f"Unsupported PLY format: {fmt}. Only binary_little_endian and ascii are supported.")

        elif parts[0] == "element":
            in_vertex_element = (parts[1] == "vertex")
            if in_vertex_element:
                vertex_count = int(parts[2])

        elif parts[0] == "property" and in_vertex_element:
            if parts[1] == "list":
                raise ValueError("List properties in vertex element are not supported.")
            dtype_str = parts[1]
            prop_name = parts[2]
            properties.append((prop_name, dtype_str))

    header_end = f.tell()
    return fmt, properties, header_end, vertex_count


_PLY_TO_NUMPY = {
    "float":   "f4", "float32": "f4",
    "double":  "f8", "float64": "f8",
    "uchar":   "u1", "uint8":   "u1",
    "char":    "i1", "int8":    "i1",
    "ushort":  "u2", "uint16":  "u2",
    "short":   "i2", "int16":   "i2",
    "uint":    "u4", "uint32":  "u4",
    "int":     "i4", "int32":   "i4",
}


def ply_dtype(properties):
    """Convert PLY property list to a numpy structured dtype."""
    fields = []
    for name, typ in properties:
        np_type = _PLY_TO_NUMPY.get(typ)
        if np_type is None:
            raise ValueError(f"Unsupported PLY property type: {typ}")
        fields.append((name, f"<{np_type}"))  # little-endian
    return np.dtype(fields)


def read_ply(path):
    """Read a PLY file and return (structured numpy array, format, properties, header_bytes)."""
    with open(path, "rb") as f:
        fmt, properties, header_end, vertex_count = parse_ply_header(f)

        # Validate x, y, z.
        prop_names = [p[0] for p in properties]
        for axis in ("x", "y", "z"):
            if axis not in prop_names:
                raise ValueError(f"PLY file missing required '{axis}' property.")

        # Save raw header bytes for reconstruction.
        f.seek(0)
        header_bytes = f.read(header_end)
        # Position file after header for data read.

        dt = ply_dtype(properties)

        if fmt == "binary_little_endian":
            data = np.fromfile(f, dtype=dt, count=vertex_count)
        elif fmt == "ascii":
            # Read line by line into structured array.
            data = np.empty(vertex_count, dtype=dt)
            for i in _progress(range(vertex_count), "Reading ASCII PLY", vertex_count):
                line = f.readline().decode("ascii").strip().split()
                for j, (name, _) in enumerate(properties):
                    data[name][i] = dt[name].type(line[j])
        else:
            raise ValueError(f"Unsupported format: {fmt}")

    if len(data) != vertex_count:
        raise ValueError(f"Expected {vertex_count} vertices, got {len(data)}.")

    return data, fmt, properties, header_bytes


def write_ply(path, data, fmt, properties):
    """Write a structured numpy array back to a PLY file, preserving all properties."""
    with open(path, "wb") as f:
        # Write header.
        f.write(b"ply\n")
        f.write(f"format {fmt} 1.0\n".encode())
        f.write(f"element vertex {len(data)}\n".encode())
        for name, typ in properties:
            f.write(f"property {typ} {name}\n".encode())
        f.write(b"end_header\n")

        if fmt == "binary_little_endian":
            data.tofile(f)
        elif fmt == "ascii":
            for row in _progress(data, "Writing ASCII PLY", len(data)):
                vals = []
                for name, _ in properties:
                    v = row[name]
                    if np.issubdtype(type(v), np.floating):
                        vals.append(f"{v:.8g}")
                    else:
                        vals.append(str(v))
                f.write((" ".join(vals) + "\n").encode())


# ---------------------------------------------------------------------------
# Progress helper
# ---------------------------------------------------------------------------

def _progress(iterable, desc, total):
    if tqdm is not None:
        return tqdm(iterable, desc=desc, total=total, unit="splats")
    # Plain fallback: print every 25%.
    if total <= 0:
        yield from iterable
        return
    milestones = {int(total * p) for p in (0.25, 0.5, 0.75, 1.0)}

    def _gen():
        for i, item in enumerate(iterable):
            yield item
            if i in milestones:
                pct = (i + 1) / total * 100
                print(f"  {desc}: {pct:.0f}%", file=sys.stderr)
    yield from _gen()


# ---------------------------------------------------------------------------
# Grid partitioning
# ---------------------------------------------------------------------------

def parse_grid(grid_str):
    parts = grid_str.lower().split("x")
    if len(parts) != 2:
        raise ValueError(f"Grid must be NxM, got '{grid_str}'.")
    return int(parts[0]), int(parts[1])


def compute_zone_assignments(positions, grid, axis, overlap, min_splats):
    """
    Assign each splat to one or more grid zones. Returns:
      - zone_indices: list of (row, col) for each zone
      - zone_masks: list of boolean arrays, one per zone
    """
    rows, cols = grid

    if axis == "xz":
        ax0 = positions[:, 0]  # x
        ax1 = positions[:, 2]  # z
    elif axis == "xyz":
        # For 3-axis: use x for cols, z for rows (y ignored for grid — could extend)
        ax0 = positions[:, 0]
        ax1 = positions[:, 2]
    else:
        raise ValueError(f"Unsupported axis: {axis}")

    mn0, mx0 = ax0.min(), ax0.max()
    mn1, mx1 = ax1.min(), ax1.max()
    span0 = mx0 - mn0
    span1 = mx1 - mn1

    if span0 <= 0 or span1 <= 0:
        raise ValueError("Degenerate bounding box — all splats at the same position?")

    cell_w = span0 / cols
    cell_h = span1 / rows
    overlap0 = cell_w * overlap
    overlap1 = cell_h * overlap

    zone_indices = []
    zone_masks = []

    for r in range(rows):
        for c in range(cols):
            lo0 = mn0 + c * cell_w - overlap0
            hi0 = mn0 + (c + 1) * cell_w + overlap0
            lo1 = mn1 + r * cell_h - overlap1
            hi1 = mn1 + (r + 1) * cell_h + overlap1

            mask = (ax0 >= lo0) & (ax0 < hi0) & (ax1 >= lo1) & (ax1 < hi1)
            zone_indices.append((r, c))
            zone_masks.append(mask)

    # Merge zones below min_splats into nearest neighbor.
    changed = True
    while changed:
        changed = False
        for i in range(len(zone_masks)):
            if zone_masks[i] is None:
                continue
            count = zone_masks[i].sum()
            if count >= min_splats:
                continue
            if count == 0:
                zone_masks[i] = None
                changed = True
                continue

            # Find nearest active neighbor by grid distance.
            ri, ci = zone_indices[i]
            best_j = -1
            best_dist = float("inf")
            for j in range(len(zone_masks)):
                if j == i or zone_masks[j] is None:
                    continue
                rj, cj = zone_indices[j]
                d = abs(ri - rj) + abs(ci - cj)
                if d < best_dist:
                    best_dist = d
                    best_j = j

            if best_j >= 0:
                zone_masks[best_j] = zone_masks[best_j] | zone_masks[i]
                zone_masks[i] = None
                changed = True

    # Compact.
    final_indices = []
    final_masks = []
    for i in range(len(zone_masks)):
        if zone_masks[i] is not None and zone_masks[i].sum() > 0:
            final_indices.append(zone_indices[i])
            final_masks.append(zone_masks[i])

    return final_indices, final_masks


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main():
    parser = argparse.ArgumentParser(
        description="Spatially partition a Gaussian Splat PLY into grid zones.")
    parser.add_argument("--input", required=True, help="Path to source PLY file.")
    parser.add_argument("--output-dir", required=True, help="Directory for output PLYs.")
    parser.add_argument("--grid", default="3x3", help="NxM grid (default: 3x3).")
    parser.add_argument("--axis", default="xz", choices=["xz", "xyz"],
                        help="Axes to split on (default: xz).")
    parser.add_argument("--min-splats", type=int, default=50000,
                        help="Minimum splats per zone before merging (default: 50000).")
    parser.add_argument("--overlap", type=float, default=0.05,
                        help="Fractional overlap between zones (default: 0.05).")
    parser.add_argument("--prefix", default=None,
                        help="Output filename prefix (default: source stem).")
    args = parser.parse_args()

    input_path = Path(args.input)
    if not input_path.exists():
        print(f"Error: input file not found: {input_path}", file=sys.stderr)
        sys.exit(1)

    output_dir = Path(args.output_dir)
    try:
        output_dir.mkdir(parents=True, exist_ok=True)
    except OSError as e:
        print(f"Error: cannot create output directory: {e}", file=sys.stderr)
        sys.exit(1)

    # Check output dir is writable.
    test_file = output_dir / ".write_test"
    try:
        test_file.touch()
        test_file.unlink()
    except OSError:
        print(f"Error: output directory is not writable: {output_dir}", file=sys.stderr)
        sys.exit(1)

    prefix = args.prefix or input_path.stem
    grid = parse_grid(args.grid)

    print(f"Reading {input_path}...")
    data, fmt, properties, _ = read_ply(str(input_path))
    total_count = len(data)
    print(f"  {total_count:,} splats, {len(properties)} properties, format={fmt}")

    # Extract positions.
    positions = np.column_stack([data["x"], data["y"], data["z"]]).astype(np.float64)
    assert len(positions) == total_count, "Position count mismatch after parse."

    print(f"Partitioning into {grid[0]}x{grid[1]} grid on {args.axis} axes "
          f"(overlap={args.overlap}, min-splats={args.min_splats})...")
    zone_indices, zone_masks = compute_zone_assignments(
        positions, grid, args.axis, args.overlap, args.min_splats)

    if len(zone_indices) == 0:
        print("Error: no zones produced. Check your --min-splats setting.", file=sys.stderr)
        sys.exit(1)

    # Write output files.
    print(f"\nWriting {len(zone_indices)} zone(s)...\n")
    results = []
    total_output_splats = 0

    for zone_idx, ((r, c), mask) in enumerate(zip(zone_indices, zone_masks)):
        zone_data = data[mask]
        count = len(zone_data)
        total_output_splats += count

        out_name = f"{prefix}_zone_{r}_{c}.ply"
        out_path = output_dir / out_name

        # Compute bounds.
        zx = zone_data["x"]
        zy = zone_data["y"]
        zz = zone_data["z"]
        bounds_str = (f"[{zx.min():.2f}..{zx.max():.2f}, "
                      f"{zy.min():.2f}..{zy.max():.2f}, "
                      f"{zz.min():.2f}..{zz.max():.2f}]")

        write_ply(str(out_path), zone_data, fmt, properties)
        results.append((out_name, count, bounds_str))
        print(f"  {out_name}: {count:,} splats")

    # Summary table.
    print(f"\n{'Zone':<10} {'Splat Count':>12} {'Bounds':<50} {'Output File'}")
    print("-" * 100)
    for name, count, bounds in results:
        r, c = name.split("_zone_")[1].replace(".ply", "").split("_")
        print(f"({r},{c})     {count:>12,} {bounds:<50} {name}")

    print(f"\nTotal input splats:  {total_count:,}")
    print(f"Sum of zone counts:  {total_output_splats:,}")
    if total_output_splats > total_count:
        print(f"  (sum > total because {total_output_splats - total_count:,} splats are duplicated "
              f"in overlap regions between adjacent zones)")
    elif total_output_splats < total_count:
        print(f"  WARNING: {total_count - total_output_splats:,} splats were not assigned to any zone!")

    print(f"\nDone. {len(results)} PLY files written to {output_dir}/")


if __name__ == "__main__":
    main()
