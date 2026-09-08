using System;

namespace Server.Custom.MapExport
{
    /// <summary>
    /// The isometric projection, and the facet-global pixel grid the art tiles are cut from.
    ///
    /// THE FORMULA IS NOT INVENTED HERE. It is Ultima/Multis.cs:502-511, which is the one working
    /// isometric renderer already in this tree:
    ///
    ///     px = (x - y) * 22;  py = (x + y) * 22;
    ///     px -= bmp.Width / 2;  py -= tile.Z &lt;&lt; 2;  py -= bmp.Height;
    ///
    /// Read as a rule rather than as four statements, that is: a tile's anchor point is
    /// ((x-y)*22, (x+y)*22 - 4z), and any sprite is drawn with its BOTTOM CENTRE on that point.
    /// Land art is 44x44, so a land tile lands at (ix-22, iy-44) - the same rule, not a special
    /// case. tools/editor/js/iso.js is the second copy of this, and iso.test.js pins them together.
    ///
    /// Two consequences shape everything downstream:
    ///
    ///   - There are 22 iso pixels per world tile PER AXIS, so at z=0 the projection is linear and
    ///     the editor can draw its radar underlay with a plain canvas setTransform.
    ///   - Larger (x + y) is nearer the viewer, so painter's order is diagonals ascending.
    ///
    /// THE WHOLE FACET CANNOT BE RENDERED. Trammel's canvas is (7168+4096)*22 = 247,808px on each
    /// side - 61 gigapixels per floor. That is why there is no batch export: the bridge asks for
    /// one tile at a time and the renderer walks only the columns whose sprites can touch it.
    /// </summary>
    internal static class IsoTransform
    {
        public const int HalfWidth = 22;
        public const int HalfHeight = 22;
        public const int ZStep = 4;

        /// <summary>Land art is always 44x44 (Ultima/Art.cs:528-529).</summary>
        public const int LandArtSize = 44;

        /// <summary>
        /// The bleed used when deciding which world columns can touch a tile.
        ///
        /// Deliberately generous rather than measured. A sprite is rejected by a bounding-box test
        /// long before any pixel is touched, so over-wide bounds cost a loop iteration each; too
        /// narrow bounds cost a tree that is silently missing the top of every wall.
        /// </summary>
        public const int MaxSpriteWidth = 256;

        public const int MaxSpriteHeight = 512;

        /// <summary>Z is an sbyte, so +127 lifts a sprite by 508px and -128 drops it by 512.</summary>
        public const int ZLift = 128 * ZStep;

        public const int HeadroomTop = ZLift + MaxSpriteHeight;
        public const int HeadroomBottom = ZLift;

        /// <summary>
        /// How many of the deepest levels are rendered as art at all.
        ///
        /// Not a taste decision: a tile at level L covers 256 * 2^(max-L) iso pixels, so each step
        /// out quadruples the world area one render has to walk. Four levels is 1:1 through 1:8,
        /// where the coarsest is about 93x93 world tiles and a second of work. One step further is
        /// four seconds, and level 0 is the entire facet. Below this depth the editor draws radar,
        /// which is what radar is good at.
        /// </summary>
        public const int ArtLevelDepth = 4;

        /// <summary>
        /// How many of the deepest levels carry separate floors. Below this there is one composite
        /// - at 1:4 a storey is five pixels tall and peeling it off shows nothing.
        /// </summary>
        public const int FloorLevelDepth = 2;

        public static int IsoX(int x, int y)
        {
            return (x - y) * HalfWidth;
        }

        public static int IsoY(int x, int y, int z)
        {
            return ((x + y) * HalfHeight) - (z * ZStep);
        }

        /// <summary>
        /// The iso X of canvas column 0. Negative, because a tile at (0, height-1) projects to a
        /// large negative iso X and the canvas has to start left of it.
        /// </summary>
        public static int OriginX(int facetHeight)
        {
            return -(facetHeight * HalfWidth);
        }

        /// <summary>The iso Y of canvas row 0. Negative, to leave room for lifted, tall sprites.</summary>
        public static int OriginY()
        {
            return -HeadroomTop;
        }

        public static int CanvasWidth(int facetWidth, int facetHeight)
        {
            return (facetWidth + facetHeight) * HalfWidth;
        }

        public static int CanvasHeight(int facetWidth, int facetHeight)
        {
            return ((facetWidth + facetHeight) * HalfHeight) + HeadroomTop + HeadroomBottom;
        }

        /// <summary>
        /// Ceil-halve until the canvas fits one tile - the same rule as TilePyramid.LevelCount and
        /// view.js maxZoomFor, and it must stay the same rule or the editor asks for tiles at a
        /// level the renderer does not believe in. Returns the deepest level index; level 0 is the
        /// whole canvas in one tile and the deepest is one screen pixel per iso pixel.
        /// </summary>
        public static int MaxLevel(int facetWidth, int facetHeight, int tileSize)
        {
            int width = CanvasWidth(facetWidth, facetHeight);
            int height = CanvasHeight(facetWidth, facetHeight);
            int levels = 0;

            while (width > tileSize || height > tileSize)
            {
                width = (width + 1) / 2;
                height = (height + 1) / 2;
                levels++;
            }

            return levels;
        }

        /// <summary>Iso pixels per rendered pixel at a level. 1 at the deepest level.</summary>
        public static int Divisor(int level, int maxLevel)
        {
            return 1 << (maxLevel - level);
        }

        /// <summary>
        /// The world columns whose sprites can reach a canvas rectangle, as a range of diagonals
        /// (d = x + y) and a range of anti-diagonals (e = x - y). x and y recover as (d+e)/2 and
        /// (d-e)/2, which is only whole where d and e have the same parity - hence the stride of
        /// two when walking e.
        /// </summary>
        public static void Footprint(
            int canvasX,
            int canvasY,
            int span,
            int facetWidth,
            int facetHeight,
            out int dMin,
            out int dMax,
            out int eMin,
            out int eMax)
        {
            int ix0 = canvasX + OriginX(facetHeight);
            int ix1 = ix0 + span;
            int iy0 = canvasY + OriginY();
            int iy1 = iy0 + span;

            // A sprite anchored at (ax, ay) covers [ax - w/2, ax + w/2) x [ay - h, ay), so it can
            // touch the rectangle when its anchor is inside these widened bounds. The anchor's
            // (x + y) * 22 is ay + 4z, and 4z spans [-ZLift, ZLift).
            int halfSprite = (MaxSpriteWidth / 2) + 1;

            dMin = FloorDiv(iy0 - ZLift, HalfHeight);
            dMax = CeilDiv(iy1 + MaxSpriteHeight + ZLift, HalfHeight);

            eMin = FloorDiv(ix0 - halfSprite, HalfWidth);
            eMax = CeilDiv(ix1 + halfSprite, HalfWidth);

            // Nothing outside the facet exists, so clamp both ranges to what x and y can reach.
            int dLimit = (facetWidth - 1) + (facetHeight - 1);

            if (dMin < 0)
            {
                dMin = 0;
            }

            if (dMax > dLimit)
            {
                dMax = dLimit;
            }

            int eLow = -(facetHeight - 1);
            int eHigh = facetWidth - 1;

            if (eMin < eLow)
            {
                eMin = eLow;
            }

            if (eMax > eHigh)
            {
                eMax = eHigh;
            }
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;

            if (value % divisor != 0 && ((value < 0) != (divisor < 0)))
            {
                quotient--;
            }

            return quotient;
        }

        private static int CeilDiv(int value, int divisor)
        {
            int quotient = value / divisor;

            if (value % divisor != 0 && ((value < 0) == (divisor < 0)))
            {
                quotient++;
            }

            return quotient;
        }
    }
}
