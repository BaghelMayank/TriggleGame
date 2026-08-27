using UnityEngine;

namespace Triggle.Grid
{
    /// <summary>
    /// Pure, allocation-free helpers for the axial <c>(q, r)</c> triangular lattice.
    /// </summary>
    /// <remarks>
    /// Layout used throughout the project:
    /// <code>
    /// X = pegSpacing * sqrt(3) * (q + r / 2)
    /// Z = pegSpacing * 1.5     *  r
    /// Y = 0
    /// </code>
    /// All six axial neighbour directions map to the same world distance
    /// (<c>sqrt(3) * pegSpacing</c>), which is what makes the lattice equilateral.
    /// </remarks>
    public static class AxialMath
    {
        /// <summary>sqrt(3), the X scale factor of the layout.</summary>
        public const float Sqrt3 = 1.7320508f;

        /// <summary>
        /// The six neighbour directions in cyclic (60 degree) order. Consecutive entries are themselves
        /// one lattice step apart. The six form three opposite pairs, which is why there are only three
        /// distinct line directions a straight rubber band can follow.
        /// </summary>
        public static readonly Vector2Int[] Directions =
        {
            new Vector2Int(+1,  0),
            new Vector2Int(+1, -1),
            new Vector2Int( 0, -1),
            new Vector2Int(-1,  0),
            new Vector2Int(-1, +1),
            new Vector2Int( 0, +1)
        };

        /// <summary>World-space length of one lattice edge for the given peg spacing.</summary>
        public static float UnitEdgeLength(float pegSpacing) => Sqrt3 * pegSpacing;

        /// <summary>Converts axial coordinates to a position on the board plane (Y = 0).</summary>
        public static Vector3 ToWorld(Vector2Int coord, float pegSpacing)
        {
            float x = pegSpacing * Sqrt3 * (coord.x + coord.y * 0.5f);
            float z = pegSpacing * 1.5f * coord.y;
            return new Vector3(x, 0f, z);
        }

        /// <summary>Converts axial coordinates to a position lifted <paramref name="height"/> above the board.</summary>
        public static Vector3 ToWorld(Vector2Int coord, float pegSpacing, float height)
        {
            Vector3 p = ToWorld(coord, pegSpacing);
            p.y = height;
            return p;
        }

        /// <summary>
        /// Inverse of <see cref="ToWorld"/>: nearest lattice coordinate to an arbitrary world point.
        /// Uses cube rounding so the result is always a valid axial coordinate.
        /// </summary>
        public static Vector2Int FromWorld(Vector3 world, float pegSpacing)
        {
            if (pegSpacing <= 0f) return Vector2Int.zero;

            float r = world.z / (1.5f * pegSpacing);
            float q = world.x / (Sqrt3 * pegSpacing) - r * 0.5f;
            return RoundToAxial(q, r);
        }

        /// <summary>Rounds fractional axial coordinates to the nearest lattice node via cube rounding.</summary>
        public static Vector2Int RoundToAxial(float q, float r)
        {
            float x = q;
            float z = r;
            float y = -x - z;

            int rx = Mathf.RoundToInt(x);
            int ry = Mathf.RoundToInt(y);
            int rz = Mathf.RoundToInt(z);

            float dx = Mathf.Abs(rx - x);
            float dy = Mathf.Abs(ry - y);
            float dz = Mathf.Abs(rz - z);

            // Discard the component with the largest rounding error to restore x + y + z == 0.
            if (dx > dy && dx > dz) rx = -ry - rz;
            else if (dz > dy) rz = -rx - ry;

            return new Vector2Int(rx, rz);
        }

        /// <summary>Lattice (hex) distance in whole steps between two axial coordinates.</summary>
        public static int Distance(Vector2Int a, Vector2Int b)
        {
            int dq = a.x - b.x;
            int dr = a.y - b.y;
            return (Mathf.Abs(dq) + Mathf.Abs(dr) + Mathf.Abs(dq + dr)) / 2;
        }

        /// <summary>True when the two coordinates are exactly one unit edge apart.</summary>
        public static bool AreAdjacent(Vector2Int a, Vector2Int b) => Distance(a, b) == 1;

        /// <summary>Neighbour coordinate in one of the six <see cref="Directions"/>.</summary>
        public static Vector2Int Neighbour(Vector2Int coord, int directionIndex)
        {
            Vector2Int d = Directions[((directionIndex % 6) + 6) % 6];
            return new Vector2Int(coord.x + d.x, coord.y + d.y);
        }

        /// <summary>
        /// Exact lattice midpoint of two coordinates. Only meaningful when both components of the
        /// delta are even (i.e. the two nodes are an even number of steps apart on a straight line).
        /// </summary>
        public static Vector2Int Midpoint(Vector2Int a, Vector2Int b) =>
            new Vector2Int((a.x + b.x) / 2, (a.y + b.y) / 2);

        /// <summary>True when the coordinate lies inside the hexagon of the given radius centred on origin.</summary>
        public static bool IsInsideHex(Vector2Int coord, int radius) =>
            Distance(coord, Vector2Int.zero) <= radius;

        /// <summary>
        /// Number of lattice nodes inside a hexagon of radius <paramref name="radius"/>
        /// (centred hexagonal number: <c>3R^2 + 3R + 1</c>).
        /// </summary>
        public static int PegCountForRadius(int radius) => 3 * radius * radius + 3 * radius + 1;

        /// <summary>Signed area helper used to classify a unit cell as up- or down-pointing.</summary>
        public static float SignedArea(Vector3 a, Vector3 b, Vector3 c) =>
            0.5f * ((b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z));
    }
}
