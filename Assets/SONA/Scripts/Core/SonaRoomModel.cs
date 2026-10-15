// SONA Quest port: the editable test rooms (scene.py): presets, floor-anchored boxes and
// wall panels, custom rooms and the random-box localization challenge.
//
// The model keeps scene.py's top-down frame internally (x = right, y = forward, z = up,
// yaw in degrees clockwise from +y) so it can be checked line-by-line against the Python,
// and converts to Unity's frame (x, z, y) only when it hands out geometry.
using System;
using System.Collections.Generic;

namespace Sona.Core
{
    public enum WallMode { Open, Closed, NoWalls }

    public enum ObjectKind { Box, Panel }

    public sealed class RoomPresetInfo
    {
        public readonly string Id;
        public readonly string Label;
        public readonly double Width;
        public readonly double Depth;
        public readonly double Height;
        public readonly bool Enclosed;
        public readonly bool WallFree;

        public RoomPresetInfo(string id, string label, double width, double depth, double height, bool enclosed, bool wallFree = false)
        {
            Id = id;
            Label = label;
            Width = width;
            Depth = depth;
            Height = height;
            Enclosed = enclosed;
            WallFree = wallFree;
        }
    }

    [Serializable]
    public sealed class RoomSpec
    {
        public double Width;
        public double Depth;
        public double Height;
        public bool Enclosed;
        public bool WallFree;

        public RoomSpec(double width, double depth, double height, bool enclosed, bool wallFree)
        {
            if (!(width > 0) || !(depth > 0) || !(height > 0)) throw new ArgumentException("room dimensions must be positive");
            if (wallFree && enclosed) throw new ArgumentException("a wall-free room cannot be enclosed");
            Width = width;
            Depth = depth;
            Height = height;
            Enclosed = enclosed;
            WallFree = wallFree;
        }

        public double XMin { get { return -Width / 2.0; } }
        public double XMax { get { return Width / 2.0; } }
        public double YMin { get { return 0.0; } }
        public double YMax { get { return Depth; } }

        public WallMode Walls { get { return WallFree ? WallMode.NoWalls : (Enclosed ? WallMode.Closed : WallMode.Open); } }
    }

    [Serializable]
    public sealed class ObjectSpec
    {
        public string Id;
        public string Name;
        public ObjectKind Kind;
        public double X;            // top-down centre, scene.py frame
        public double Y;
        public double SizeRight;    // box width  / panel length
        public double SizeForward;  // box depth  / panel thickness
        public double Height;
        public double YawDegrees;

        public static ObjectSpec Box(string id, string name, double x, double y, double width = 1.0, double depth = 1.0,
                                     double height = 1.0, double yaw = 0.0)
        {
            return new ObjectSpec { Id = id, Name = name, Kind = ObjectKind.Box, X = x, Y = y, SizeRight = width, SizeForward = depth, Height = height, YawDegrees = yaw };
        }

        public static ObjectSpec Panel(string id, string name, double x, double y, double length = 1.5, double thickness = 0.08,
                                       double height = 1.5, double yaw = 0.0)
        {
            return new ObjectSpec { Id = id, Name = name, Kind = ObjectKind.Panel, X = x, Y = y, SizeRight = length, SizeForward = thickness, Height = height, YawDegrees = yaw };
        }

        public ObjectSpec Clone() { return (ObjectSpec)MemberwiseClone(); }
    }

    public sealed class RoomLayout
    {
        public string PresetId;
        public RoomSpec Room;
        public double ListenerX;
        public double ListenerY;
        public double ListenerZ = 1.5;
        public double ListenerYawDegrees;
        public List<ObjectSpec> Objects = new List<ObjectSpec>();
        public int NextBox = 1;      // scene.py _next_box / _next_panel: ids keep counting after deletes
        public int NextPanel = 1;

        public ObjectSpec Find(string id)
        {
            for (int i = 0; i < Objects.Count; i++)
            {
                if (Objects[i].Id == id) return Objects[i];
            }
            return null;
        }
    }

    public static class SonaRoomModel
    {
        public const int MaxObjects = 25;
        public const string DefaultPresetId = "open_demo";
        public const string CustomPresetId = "custom";
        public const double RandomBoxSize = 1.0;
        public const double RandomBoxMinDistance = 1.5;
        public const double MinCustomWidth = 2.0, MaxCustomWidth = 20.0;
        public const double MinCustomDepth = 2.0, MaxCustomDepth = 20.0;
        public const double MinCustomHeight = 1.5, MaxCustomHeight = 6.0;

        /// <summary>Presets in the order the Python editor shows them.</summary>
        public static readonly RoomPresetInfo[] Presets =
        {
            new RoomPresetInfo("open_compact", "Open Compact 4 x 4 x 3 m", 4.0, 4.0, 3.0, false),
            new RoomPresetInfo("enclosed_compact", "Enclosed Compact 4 x 4 x 3 m", 4.0, 4.0, 3.0, true),
            new RoomPresetInfo("open_demo", "Open Demo 5 x 6 x 3 m", 5.0, 6.0, 3.0, false),
            new RoomPresetInfo("enclosed_demo", "Enclosed Demo 5 x 6 x 3 m", 5.0, 6.0, 3.0, true),
            new RoomPresetInfo("no_walls_demo", "No Walls Demo 5 x 6 x 3 m", 5.0, 6.0, 3.0, false, true),
            new RoomPresetInfo("open_large", "Open Large 8 x 10 x 3 m", 8.0, 10.0, 3.0, false),
            new RoomPresetInfo("enclosed_large", "Enclosed Large 8 x 10 x 3 m", 8.0, 10.0, 3.0, true),
        };

        public static RoomPresetInfo FindPreset(string id)
        {
            for (int i = 0; i < Presets.Length; i++)
            {
                if (Presets[i].Id == id) return Presets[i];
            }
            throw new ArgumentException("unknown room preset: " + id);
        }

        // ------------------------------------------------------------------ construction

        /// <summary>scene._make_room_scene: listener at the front edge, Box 1 and Panel 1 ahead.</summary>
        public static RoomLayout MakeRoomScene(RoomSpec room, string presetId)
        {
            var layout = new RoomLayout
            {
                PresetId = presetId,
                Room = room,
                ListenerX = 0.0,
                ListenerY = room.Enclosed ? 0.5 : 0.0,
                ListenerZ = 1.5,
                ListenerYawDegrees = 0.0,
            };
            layout.Objects.Add(ObjectSpec.Box("box-1", "Box 1", 0.0, 0.48333333333333334 * room.Depth, 1.0, 1.0, 1.0, 0.0));
            layout.Objects.Add(ObjectSpec.Panel("panel-1", "Panel 1", 0.0, 0.7666666666666667 * room.Depth, 0.8, 0.08, 1.5, 0.0));
            for (int i = 0; i < layout.Objects.Count; i++) ClampObject(room, layout.Objects[i]);
            layout.NextBox = 2;
            layout.NextPanel = 2;
            return layout;
        }

        /// <summary>Any size and wall mode (the editor's Custom Room), with or without Box 1 and Panel 1.</summary>
        public static RoomLayout MakeRoom(double width, double depth, double height, WallMode walls, bool starterObjects)
        {
            var room = new RoomSpec(width, depth, height, walls == WallMode.Closed, walls == WallMode.NoWalls);
            RoomLayout layout = MakeRoomScene(room, CustomPresetId);
            if (!starterObjects) layout.Objects.Clear();
            return layout;
        }

        /// <summary>
        /// Resize the room or change its walls while keeping the objects: they are clamped inside the new
        /// room (heights capped at the ceiling). Objects that no longer fit are removed and returned.
        /// </summary>
        public static List<ObjectSpec> ChangeRoom(RoomLayout layout, double width, double depth, double height, WallMode walls)
        {
            layout.Room = new RoomSpec(width, depth, height, walls == WallMode.Closed, walls == WallMode.NoWalls);
            layout.ListenerY = layout.Room.Enclosed ? 0.5 : 0.0;
            var removed = new List<ObjectSpec>();
            for (int i = layout.Objects.Count - 1; i >= 0; i--)
            {
                ObjectSpec o = layout.Objects[i];
                if (o.Height > height) o.Height = height;
                try
                {
                    ClampObject(layout.Room, o);
                }
                catch (ArgumentException)
                {
                    removed.Add(o);
                    layout.Objects.RemoveAt(i);
                }
            }
            return removed;
        }

        /// <summary>scene.add_box / add_panel: next id and name, default size, clamped inside the room.</summary>
        public static ObjectSpec AddObject(RoomLayout layout, ObjectKind kind, double x, double y)
        {
            if (layout.Objects.Count >= MaxObjects)
                throw new ArgumentException("A scene can contain at most " + MaxObjects + " objects");
            ObjectSpec o;
            if (kind == ObjectKind.Box)
            {
                int n = layout.NextBox++;
                o = ObjectSpec.Box("box-" + n, "Box " + n, x, y);
            }
            else
            {
                int n = layout.NextPanel++;
                o = ObjectSpec.Panel("panel-" + n, "Panel " + n, x, y);
            }
            if (o.Height > layout.Room.Height) o.Height = layout.Room.Height;
            ClampObject(layout.Room, o);
            layout.Objects.Add(o);
            return o;
        }

        public static RoomLayout MakePreset(string presetId)
        {
            RoomPresetInfo p = FindPreset(presetId);
            return MakeRoomScene(new RoomSpec(p.Width, p.Depth, p.Height, p.Enclosed, p.WallFree), p.Id);
        }

        public static RoomLayout MakeCustomRoom(double width, double depth, double height, WallMode walls)
        {
            Bounded(width, "room width", MinCustomWidth, MaxCustomWidth);
            Bounded(depth, "room depth", MinCustomDepth, MaxCustomDepth);
            Bounded(height, "room height", MinCustomHeight, MaxCustomHeight);
            var room = new RoomSpec(width, depth, height, walls == WallMode.Closed, walls == WallMode.NoWalls);
            return MakeRoomScene(room, CustomPresetId);
        }

        static void Bounded(double value, string label, double lo, double hi)
        {
            if (!SonaMath.IsFinite(value) || value < lo || value > hi)
                throw new ArgumentException(string.Format("{0} must be between {1:g} and {2:g} m", label, lo, hi));
        }

        /// <summary>
        /// scene._make_random_box_scene: one hidden 1 x 1 x 1 m box, centre uniform over the valid
        /// in-room rectangle, at least <paramref name="minListenerDistance"/> (planar) from the listener.
        /// Returns the new box; the caller replaces the layout's objects with it.
        /// </summary>
        public static ObjectSpec MakeRandomBox(RoomSpec room, double listenerX, double listenerY, Random rng,
                                               double minListenerDistance = RandomBoxMinDistance)
        {
            if (!(minListenerDistance >= 0)) throw new ArgumentException("minimum listener distance cannot be negative");
            double w = RandomBoxSize, d = RandomBoxSize;
            double xLow = room.XMin + w / 2.0, xHigh = room.XMax - w / 2.0;
            double yLow = room.YMin + d / 2.0, yHigh = room.YMax - d / 2.0;
            double[][] corners =
            {
                new[] { xLow, yLow }, new[] { xLow, yHigh }, new[] { xHigh, yLow }, new[] { xHigh, yHigh },
            };
            Func<double, double, double> dist = (x, y) => SonaMath.Hypot(x - listenerX, y - listenerY);
            double[] fallback = corners[0];
            for (int i = 1; i < corners.Length; i++)
            {
                if (dist(corners[i][0], corners[i][1]) > dist(fallback[0], fallback[1])) fallback = corners[i];
            }
            if (dist(fallback[0], fallback[1]) + 1e-9 < minListenerDistance)
                throw new ArgumentException("this room is too small for the requested listener clearance");

            double px = fallback[0], py = fallback[1];
            for (int attempt = 0; attempt < 256; attempt++)
            {
                double cx = xLow + (xHigh - xLow) * rng.NextDouble();
                double cy = yLow + (yHigh - yLow) * rng.NextDouble();
                if (dist(cx, cy) >= minListenerDistance)
                {
                    px = cx;
                    py = cy;
                    break;
                }
            }
            return ObjectSpec.Box("box-1", "Mystery Box", px, py, w, d, RandomBoxSize, 0.0);
        }

        // ------------------------------------------------------------------ geometry (scene.py frame)

        static void LocalAxes(double yawDegrees, out double rx, out double ry, out double fx, out double fy)
        {
            double a = yawDegrees * SonaMath.Deg2Rad;
            rx = Math.Cos(a);
            ry = -Math.Sin(a);
            fx = Math.Sin(a);
            fy = Math.Cos(a);
        }

        /// <summary>Footprint corners [near-left, near-right, far-right, far-left] as (x, y) pairs.</summary>
        public static double[][] Footprint(ObjectSpec o)
        {
            double rx, ry, fx, fy;
            LocalAxes(o.YawDegrees, out rx, out ry, out fx, out fy);
            double hw = o.SizeRight / 2.0, hd = o.SizeForward / 2.0;
            return new[]
            {
                new[] { o.X + (-hw * rx - hd * fx), o.Y + (-hw * ry - hd * fy) },
                new[] { o.X + (hw * rx - hd * fx), o.Y + (hw * ry - hd * fy) },
                new[] { o.X + (hw * rx + hd * fx), o.Y + (hw * ry + hd * fy) },
                new[] { o.X + (-hw * rx + hd * fx), o.Y + (-hw * ry + hd * fy) },
            };
        }

        /// <summary>scene.clamp_entity: keep the whole footprint inside the room.</summary>
        public static void ClampObject(RoomSpec room, ObjectSpec o)
        {
            double[][] fp = Footprint(o);
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < 4; i++)
            {
                double rxv = fp[i][0] - o.X, ryv = fp[i][1] - o.Y;
                minX = Math.Min(minX, rxv);
                maxX = Math.Max(maxX, rxv);
                minY = Math.Min(minY, ryv);
                maxY = Math.Max(maxY, ryv);
            }
            double xLow = room.XMin - minX, xHigh = room.XMax - maxX;
            double yLow = room.YMin - minY, yHigh = room.YMax - maxY;
            if (xLow > xHigh || yLow > yHigh) throw new ArgumentException("the object is too large to fit inside this room");
            o.X = SonaMath.Clamp(o.X, xLow, xHigh);
            o.Y = SonaMath.Clamp(o.Y, yLow, yHigh);
        }

        public static double FarthestCornerDistance(RoomLayout layout)
        {
            RoomSpec r = layout.Room;
            double[][] corners = { new[] { r.XMin, r.YMin }, new[] { r.XMax, r.YMin }, new[] { r.XMax, r.YMax }, new[] { r.XMin, r.YMax } };
            double best = 0.0;
            for (int i = 0; i < 4; i++)
            {
                best = Math.Max(best, SonaMath.Hypot(corners[i][0] - layout.ListenerX, corners[i][1] - layout.ListenerY));
            }
            return best;
        }

        // ------------------------------------------------------------------ conversion to Unity frame

        /// <summary>scene.py point (x, y=forward, z=up) to Unity (x, y=up, z=forward), shifted by <paramref name="originPy"/>.</summary>
        public static Vec3d ToUnity(double x, double y, double z, double originX = 0.0, double originY = 0.0)
        {
            return new Vec3d(x - originX, z, y - originY);
        }

        /// <summary>
        /// All surface quads of the room and its objects, in Unity's frame. Pass the listener spawn as
        /// origin to place the user at the room's listener position (what SonaRoomBuilder does);
        /// pass 0 to keep scene.py's absolute coordinates (what the parity tests do).
        /// </summary>
        public static List<SurfaceQuad> SurfaceQuads(RoomLayout layout, double originX = 0.0, double originY = 0.0)
        {
            var quads = new List<SurfaceQuad>();
            RoomSpec room = layout.Room;
            double x0 = room.XMin, x1 = room.XMax, y0 = 0.0, y1 = room.Depth, z1 = room.Height;
            Func<double, double, double, Vec3d> P = (x, y, z) => ToUnity(x, y, z, originX, originY);

            quads.Add(new SurfaceQuad(P(x0, y0, 0), P(x1, y0, 0), P(x1, y1, 0), P(x0, y1, 0),
                                      new Vec3d(0, 1, 0), "room", "floor", SonaMaterials.Floor));
            if (!room.WallFree)
            {
                quads.Add(new SurfaceQuad(P(x0, y0, 0), P(x0, y1, 0), P(x0, y1, z1), P(x0, y0, z1),
                                          new Vec3d(1, 0, 0), "room", "left-wall", null));
                quads.Add(new SurfaceQuad(P(x1, y1, 0), P(x1, y0, 0), P(x1, y0, z1), P(x1, y1, z1),
                                          new Vec3d(-1, 0, 0), "room", "right-wall", null));
                quads.Add(new SurfaceQuad(P(x0, y1, 0), P(x1, y1, 0), P(x1, y1, z1), P(x0, y1, z1),
                                          new Vec3d(0, 0, -1), "room", "back-wall", null));
                if (room.Enclosed)
                {
                    quads.Add(new SurfaceQuad(P(x1, y0, 0), P(x0, y0, 0), P(x0, y0, z1), P(x1, y0, z1),
                                              new Vec3d(0, 0, 1), "room", "front-wall", null));
                }
            }
            for (int i = 0; i < layout.Objects.Count; i++)
            {
                AppendObjectQuads(layout.Objects[i], quads, originX, originY);
            }
            return quads;
        }

        /// <summary>scene._prism_quads: bottom, top, then near/right/far/left side faces.</summary>
        public static void AppendObjectQuads(ObjectSpec o, List<SurfaceQuad> quads, double originX = 0.0, double originY = 0.0)
        {
            double[][] fp = Footprint(o);
            double h = o.Height;
            Func<int, double, Vec3d> V = (k, z) => ToUnity(fp[k][0], fp[k][1], z, originX, originY);
            double rx, ry, fx, fy;
            LocalAxes(o.YawDegrees, out rx, out ry, out fx, out fy);
            var right = new Vec3d(rx, 0, ry);
            var forward = new Vec3d(fx, 0, fy);
            string kind = o.Id;
            quads.Add(new SurfaceQuad(V(0, 0), V(3, 0), V(2, 0), V(1, 0), new Vec3d(0, -1, 0), kind, "bottom", null));
            quads.Add(new SurfaceQuad(V(0, h), V(1, h), V(2, h), V(3, h), new Vec3d(0, 1, 0), kind, "top", null));
            string[] faces = { "near", "right", "far", "left" };
            Vec3d[] normals = { -forward, right, forward, -right };
            for (int i = 0; i < 4; i++)
            {
                int nxt = (i + 1) % 4;
                quads.Add(new SurfaceQuad(V(i, 0), V(nxt, 0), V(nxt, h), V(i, h), normals[i], kind, faces[i], null));
            }
        }

        /// <summary>
        /// Unity primitives that reproduce the room, local to the listener spawn point (the Python listener
        /// position maps to the local origin, facing +Z). Quads face into the room; boxes are Cube primitives.
        /// </summary>
        public static List<PrimitivePlacement> Placements(RoomLayout layout)
        {
            var list = new List<PrimitivePlacement>();
            RoomSpec room = layout.Room;
            double lx = layout.ListenerX, ly = layout.ListenerY;
            double w = room.Width, d = room.Depth, h = room.Height;
            double zMid = d / 2.0 - ly;
            double xOff = -lx;
            list.Add(new PrimitivePlacement
            {
                Name = "Floor", Shape = PrimitiveShape.Quad, LocalPosition = new Vec3d(xOff, 0.0, zMid),
                EulerDegrees = new Vec3d(90, 0, 0), Scale = new Vec3d(w, d, 1), Tag = SonaMaterials.Floor, SourceId = "room",
            });
            if (!room.WallFree)
            {
                list.Add(new PrimitivePlacement
                {
                    Name = "Left Wall", Shape = PrimitiveShape.Quad, LocalPosition = new Vec3d(xOff - w / 2.0, h / 2.0, zMid),
                    EulerDegrees = new Vec3d(0, -90, 0), Scale = new Vec3d(d, h, 1), SourceId = "room",
                });
                list.Add(new PrimitivePlacement
                {
                    Name = "Right Wall", Shape = PrimitiveShape.Quad, LocalPosition = new Vec3d(xOff + w / 2.0, h / 2.0, zMid),
                    EulerDegrees = new Vec3d(0, 90, 0), Scale = new Vec3d(d, h, 1), SourceId = "room",
                });
                list.Add(new PrimitivePlacement
                {
                    Name = "Back Wall", Shape = PrimitiveShape.Quad, LocalPosition = new Vec3d(xOff, h / 2.0, d - ly),
                    EulerDegrees = new Vec3d(0, 0, 0), Scale = new Vec3d(w, h, 1), SourceId = "room",
                });
                if (room.Enclosed)
                {
                    list.Add(new PrimitivePlacement
                    {
                        Name = "Front Wall", Shape = PrimitiveShape.Quad, LocalPosition = new Vec3d(xOff, h / 2.0, -ly),
                        EulerDegrees = new Vec3d(0, 180, 0), Scale = new Vec3d(w, h, 1), SourceId = "room",
                    });
                }
            }
            for (int i = 0; i < layout.Objects.Count; i++) list.Add(ObjectPlacement(layout, layout.Objects[i]));
            return list;
        }

        public static PrimitivePlacement ObjectPlacement(RoomLayout layout, ObjectSpec o)
        {
            return new PrimitivePlacement
            {
                Name = o.Name,
                Shape = PrimitiveShape.Box,
                LocalPosition = new Vec3d(o.X - layout.ListenerX, o.Height / 2.0, o.Y - layout.ListenerY),
                EulerDegrees = new Vec3d(0, o.YawDegrees, 0),
                Scale = new Vec3d(o.SizeRight, o.Height, o.SizeForward),
                SourceId = o.Id,
                IsObject = true,
            };
        }

        /// <summary>Listener pose in Unity's frame (for tests and the editor WAV export).</summary>
        public static ListenerPose3D ListenerPose(RoomLayout layout, double originX = 0.0, double originY = 0.0)
        {
            return new ListenerPose3D(
                ToUnity(layout.ListenerX, layout.ListenerY, layout.ListenerZ, originX, originY),
                layout.ListenerYawDegrees * SonaMath.Deg2Rad);
        }
    }
}
