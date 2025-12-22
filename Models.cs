using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Xml;

namespace FluxionJsSceneEditor
{
    public sealed class SceneModel
    {
        public string Name { get; set; } = string.Empty;
        public CameraModel Camera { get; set; } = new CameraModel();
        public List<BaseElement> Elements { get; } = new();
    }

    public sealed class CameraModel
    {
        public string Name { get; set; } = "MainCamera";
        public double X { get; set; }
        public double Y { get; set; }
        public double Zoom { get; set; } = 1;
        public double Width { get; set; } = 1920;
        public double Height { get; set; } = 1080;
    }

    public abstract class BaseElement
    {
        public string Name { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; } = 1;
        public double Height { get; set; } = 1;

        public int Layer { get; set; } = 0;
        public bool Active { get; set; } = true;

        public abstract string ElementType { get; }

        public virtual string DisplayName => $"{ElementType}: {Name} (Layer {Layer}){(Active ? "" : " [Inactive]")}";
    }

    public sealed class SpriteElement : BaseElement
    {
        public override string ElementType => "Sprite";
        public string? ImageSrc { get; set; }
    }

    public sealed class AnimationModel
    {
        public string Name { get; set; } = "Idle";

        // REAL list of frame paths or indices
        public List<string> Frames { get; set; } = new();

        public double Speed { get; set; } = 0.1;
        public bool Loop { get; set; } = true;
        public bool Autoplay { get; set; } = false;
    }

    public sealed class AnimatedSpriteElement : BaseElement
    {
        public override string ElementType => "AnimatedSprite";
        public string? ImageSrc { get; set; } // Path to spritesheet (for spritesheet mode)
        public int FrameWidth { get; set; } // Width of a single frame in pixels
        public int FrameHeight { get; set; } // Height of a single frame in pixels
        public List<AnimationModel> Animations { get; set; } = new();
    }

    public sealed class AudioElement : BaseElement
    {
        public override string ElementType => "Audio";
        public string? Src { get; set; }
        public bool Loop { get; set; }
        public bool Autoplay { get; set; }

        public AudioElement()
        {
            Width = 0;
            Height = 0;
        }
    }

    public sealed class ClickableElement : BaseElement
    {
        public override string ElementType => "Clickable";
        public bool HasClickableArea { get; set; }
        public string? ParentName { get; set; }

        public override string DisplayName => string.IsNullOrWhiteSpace(ParentName)
            ? $"{ElementType}: {Name} (Layer {Layer})"
            : $"{ElementType}: {Name}";
    }

    public sealed class TextElement : BaseElement
    {
        public override string ElementType => "Text";

        public string? Text { get; set; }
        public double FontSize { get; set; } = 16;
        public string? FontFamily { get; set; }
        public string? Color { get; set; }

        public TextElement()
        {
            // Text nodes don't use width/height in the new engine XML.
            Width = 0;
            Height = 0;
        }
    }

    public static class SceneSerializer
    {
        public static string Serialize(SceneModel scene)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"<Scene name=\"{Escape(scene.Name)}\">");

            sb.AppendLine(
                $"    <Camera name=\"{Escape(scene.Camera.Name)}\" " +
                $"x=\"{scene.Camera.X.ToString(CultureInfo.InvariantCulture)}\" " +
                $"y=\"{scene.Camera.Y.ToString(CultureInfo.InvariantCulture)}\" " +
                $"zoom=\"{scene.Camera.Zoom.ToString(CultureInfo.InvariantCulture)}\" " +
                $"width=\"{scene.Camera.Width.ToString(CultureInfo.InvariantCulture)}\" " +
                $"height=\"{scene.Camera.Height.ToString(CultureInfo.InvariantCulture)}\" />");

            sb.AppendLine("    ");

            // Map Clickable elements to nested <ClickableArea/> inside the owning <Sprite>.
            var clickableByName = new Dictionary<string, ClickableElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var el in scene.Elements)
            {
                if (el is ClickableElement ce)
                    clickableByName[ce.Name] = ce;
            }

            var consumedClickables = new HashSet<ClickableElement>();

            foreach (var el in scene.Elements)
            {
                switch (el)
                {
                    case TextElement te:
                        sb.AppendLine(
                            $"    <Text " +
                            $"name=\"{Escape(te.Name)}\" " +
                            (te.Active ? "" : $"Active=\"false\" ") +
                            $"text=\"{Escape(te.Text ?? string.Empty)}\" " +
                            $"x=\"{te.X.ToString(CultureInfo.InvariantCulture)}\" " +
                            $"y=\"{te.Y.ToString(CultureInfo.InvariantCulture)}\" " +
                            $"fontSize=\"{te.FontSize.ToString(CultureInfo.InvariantCulture)}\" " +
                            $"fontFamily=\"{Escape(te.FontFamily ?? string.Empty)}\" " +
                            $"color=\"{Escape(te.Color ?? string.Empty)}\" " +
                            $"layer=\"{te.Layer.ToString(CultureInfo.InvariantCulture)}\" />");
                        break;

                    case AudioElement ae:
                        sb.AppendLine(
                            $"    <Audio " +
                            $"name=\"{Escape(ae.Name)}\" " +
                            (ae.Active ? "" : $"Active=\"false\" ") +
                            $"src=\"{Escape(ae.Src ?? string.Empty)}\" " +
                            $"loop=\"{ae.Loop.ToString().ToLowerInvariant()}\" " +
                            $"autoplay=\"{ae.Autoplay.ToString().ToLowerInvariant()}\" " +
                            $"layer=\"{ae.Layer.ToString(CultureInfo.InvariantCulture)}\" />");
                        break;

                    case SpriteElement se:
                        {
                            // Try to find a matching clickable.
                            ClickableElement? ce = null;

                            // Primary: clickable with ParentName matching this sprite's name
                            foreach (var cand in clickableByName.Values)
                            {
                                if (consumedClickables.Contains(cand))
                                    continue;
                                if (string.Equals(cand.ParentName, se.Name, StringComparison.OrdinalIgnoreCase) && cand.HasClickableArea)
                                {
                                    ce = cand;
                                    break;
                                }
                            }

                            // Fallback: clickable with the conventional name "<SpriteName>Hitbox" or "<SpriteName>_ClickableArea".
                            if (ce == null && clickableByName.TryGetValue(se.Name + "Hitbox", out var hitbox))
                                ce = hitbox;
                            else if (ce == null && clickableByName.TryGetValue(se.Name + "_ClickableArea", out var ca))
                                ce = ca;

                            // Last resort: any clickable with identical bounds.
                            if (ce == null)
                            {
                                foreach (var cand in clickableByName.Values)
                                {
                                    if (consumedClickables.Contains(cand))
                                        continue;
                                    if (Math.Abs(cand.X - se.X) < 1e-6 && Math.Abs(cand.Y - se.Y) < 1e-6 &&
                                        Math.Abs(cand.Width - se.Width) < 1e-6 && Math.Abs(cand.Height - se.Height) < 1e-6 &&
                                        cand.HasClickableArea)
                                    {
                                        ce = cand;
                                        break;
                                    }
                                }
                            }

                            if (ce is { HasClickableArea: true })
                            {
                                consumedClickables.Add(ce);

                                sb.AppendLine(
                                    $"    <Sprite " +
                                    $"name=\"{Escape(se.Name)}\" " +
                                    (se.Active ? "" : $"Active=\"false\" ") +
                                    $"imageSrc=\"{Escape(se.ImageSrc ?? string.Empty)}\" " +
                                    $"x=\"{se.X.ToString(CultureInfo.InvariantCulture)}\" " +
                                    $"y=\"{se.Y.ToString(CultureInfo.InvariantCulture)}\" " +
                                    $"width=\"{se.Width.ToString(CultureInfo.InvariantCulture)}\" " +
                                    $"height=\"{se.Height.ToString(CultureInfo.InvariantCulture)}\" " +
                                    $"layer=\"{se.Layer.ToString(CultureInfo.InvariantCulture)}\">");
                                sb.AppendLine($"        <ClickableArea name=\"{Escape(ce.Name)}\" />");
                                sb.AppendLine("    </Sprite>");
                            }
                            else
                            {
                                sb.AppendLine(
                                    $"    <Sprite " +
                                    $"name=\"{Escape(se.Name)}\" " +
                                    (se.Active ? "" : $"Active=\"false\" ") +
                                    $"imageSrc=\"{Escape(se.ImageSrc ?? string.Empty)}\" " +
                                    $"x=\"{se.X.ToString(CultureInfo.InvariantCulture)}\" " +
                                    $"y=\"{se.Y.ToString(CultureInfo.InvariantCulture)}\" " +
                                    $"width=\"{se.Width.ToString(CultureInfo.InvariantCulture)}\" " +
                                    $"height=\"{se.Height.ToString(CultureInfo.InvariantCulture)}\" " +
                                    $"layer=\"{se.Layer.ToString(CultureInfo.InvariantCulture)}\" />");
                            }

                            break;
                        }

                    case AnimatedSpriteElement ase:
                        {
                            // AnimatedSprite with nested Animation elements
                            sb.AppendLine(
                                $"    <AnimatedSprite " +
                                $"name=\"{Escape(ase.Name)}\" " +
                                (ase.Active ? "" : $"Active=\"false\" ") +
                                $"imageSrc=\"{Escape(ase.ImageSrc ?? string.Empty)}\" " +
                                $"x=\"{ase.X.ToString(CultureInfo.InvariantCulture)}\" " +
                                $"y=\"{ase.Y.ToString(CultureInfo.InvariantCulture)}\" " +
                                $"width=\"{ase.Width.ToString(CultureInfo.InvariantCulture)}\" " +
                                $"height=\"{ase.Height.ToString(CultureInfo.InvariantCulture)}\" " +
                                $"frameWidth=\"{ase.FrameWidth.ToString(CultureInfo.InvariantCulture)}\" " +
                                $"frameHeight=\"{ase.FrameHeight.ToString(CultureInfo.InvariantCulture)}\" " +
                                $"layer=\"{ase.Layer.ToString(CultureInfo.InvariantCulture)}\">");

                            foreach (var anim in ase.Animations)
                            {
                                sb.AppendLine(
                                    $"        <Animation " +
                                    $"name=\"{Escape(anim.Name)}\" " +
                                    $"frames=\"{Escape(string.Join(", ", anim.Frames))}\" " +
                                    $"speed=\"{anim.Speed.ToString(CultureInfo.InvariantCulture)}\" " +
                                    $"loop=\"{anim.Loop.ToString().ToLowerInvariant()}\" " +
                                    $"autoplay=\"{anim.Autoplay.ToString().ToLowerInvariant()}\" />");
                            }

                            sb.AppendLine("    </AnimatedSprite>");
                            break;
                        }

                    case ClickableElement:
                        // Clickables are serialized as nested <ClickableArea/> inside sprites.
                        break;

                    default:
                        break;
                }
            }

            sb.AppendLine("</Scene>");
            return sb.ToString();
        }

        public static SceneModel Deserialize(string xml)
        {
            var doc = new XmlDocument
            {
                XmlResolver = null
            };
            doc.LoadXml(xml);

            var sceneNode = doc.SelectSingleNode("/Scene") ?? throw new FormatException("Missing <Scene> root element.");
            var scene = new SceneModel
            {
                Name = GetAttrAnyCase(sceneNode, "name", "Name") ?? "Scene"
            };

            var cameraNode = sceneNode.SelectSingleNode("Camera") ?? sceneNode.SelectSingleNode("./Camera");
            if (cameraNode != null)
            {
                scene.Camera.Name = GetAttrAnyCase(cameraNode, "name", "Name") ?? scene.Camera.Name;
                scene.Camera.X = ParseDouble(GetAttrAnyCase(cameraNode, "x", "X"));
                scene.Camera.Y = ParseDouble(GetAttrAnyCase(cameraNode, "y", "Y"));

                var zoomAttr = GetAttrAnyCase(cameraNode, "zoom", "Zoom");
                scene.Camera.Zoom = string.IsNullOrWhiteSpace(zoomAttr) ? 1 : ParseDouble(zoomAttr);

                var wAttr = GetAttrAnyCase(cameraNode, "width", "Width");
                var hAttr = GetAttrAnyCase(cameraNode, "height", "Height");
                if (!string.IsNullOrWhiteSpace(wAttr))
                    scene.Camera.Width = ParseDouble(wAttr);
                if (!string.IsNullOrWhiteSpace(hAttr))
                    scene.Camera.Height = ParseDouble(hAttr);
            }

            // Engine format: elements are direct children of <Scene>
            // Older editor format: elements are under <Elements>
            var elementNodes = sceneNode.SelectNodes("./Sprite|./AnimatedSprite|./Audio|./Clickable|./Text|./Element")
                           ?? sceneNode.SelectNodes("./Elements/*");

            // If both exist, prefer direct children (engine format)
            if (sceneNode.SelectNodes("./Sprite|./AnimatedSprite|./Audio|./Clickable|./Text|./Element") is XmlNodeList direct && direct.Count > 0)
                elementNodes = direct;

            if (elementNodes != null)
            {
                foreach (XmlNode n in elementNodes)
                {
                    if (n.NodeType != XmlNodeType.Element)
                        continue;

                    var localName = n.LocalName;
                    var name = GetAttrAnyCase(n, "name", "Name") ?? string.Empty;

                    if (localName == "Sprite")
                    {
                        var sprite = new SpriteElement
                        {
                            Name = name,
                            X = ParseDouble(GetAttrAnyCase(n, "x", "X")),
                            Y = ParseDouble(GetAttrAnyCase(n, "y", "Y")),
                            Width = ParseDoubleOrDefault(GetAttrAnyCase(n, "width", "Width"), 1),
                            Height = ParseDoubleOrDefault(GetAttrAnyCase(n, "height", "Height"), 1),
                            ImageSrc = GetAttrAnyCase(n, "imageSrc", "ImageSrc"),
                            Layer = ParseIntOrDefault(GetAttrAnyCase(n, "layer", "Layer"), 0),
                            Active = ParseBoolOrDefault(GetAttrAnyCase(n, "Active", "active"), true)
                        };

                        scene.Elements.Add(sprite);

                        // Engine format: nested <ClickableArea /> inside <Sprite>
                        // Represent it in the editor as a separate ClickableElement with the same bounds.
                        var clickableArea = n.SelectSingleNode("./ClickableArea") ?? n.SelectSingleNode("./ClickableArea[@name]");
                        if (clickableArea != null)
                        {
                            scene.Elements.Add(new ClickableElement
                            {
                                Name = GetAttrAnyCase(clickableArea, "name", "Name") ?? (name + "_ClickableArea"),
                                X = sprite.X,
                                Y = sprite.Y,
                                Width = sprite.Width,
                                Height = sprite.Height,
                                HasClickableArea = true,
                                Layer = sprite.Layer,
                                ParentName = sprite.Name
                            });
                        }

                        continue;
                    }

                    if (localName == "AnimatedSprite")
                    {
                        var animSprite = new AnimatedSpriteElement
                        {
                            Name = name,
                            X = ParseDouble(GetAttrAnyCase(n, "x", "X")),
                            Y = ParseDouble(GetAttrAnyCase(n, "y", "Y")),
                            Width = ParseDoubleOrDefault(GetAttrAnyCase(n, "width", "Width"), 1),
                            Height = ParseDoubleOrDefault(GetAttrAnyCase(n, "height", "Height"), 1),
                            ImageSrc = GetAttrAnyCase(n, "imageSrc", "ImageSrc"),
                            FrameWidth = ParseIntOrDefault(GetAttrAnyCase(n, "frameWidth", "FrameWidth"), 32),
                            FrameHeight = ParseIntOrDefault(GetAttrAnyCase(n, "frameHeight", "FrameHeight"), 32),
                            Layer = ParseIntOrDefault(GetAttrAnyCase(n, "layer", "Layer"), 0),
                            Active = ParseBoolOrDefault(GetAttrAnyCase(n, "Active", "active"), true)
                        };

                        // Parse nested Animation elements
                        var animNodes = n.SelectNodes("./Animation");
                        if (animNodes != null)
                        {
                            foreach (XmlNode animNode in animNodes)
                            {
                                if (animNode.NodeType != XmlNodeType.Element)
                                    continue;

                                var framesStr = GetAttrAnyCase(animNode, "frames", "Frames") ?? string.Empty;
                                var framesList = new List<string>();
                                if (!string.IsNullOrWhiteSpace(framesStr))
                                {
                                    foreach (var f in framesStr.Split(','))
                                    {
                                        var trimmed = f.Trim();
                                        if (!string.IsNullOrEmpty(trimmed))
                                            framesList.Add(trimmed);
                                    }
                                }

                                animSprite.Animations.Add(new AnimationModel
                                {
                                    Name = GetAttrAnyCase(animNode, "name", "Name") ?? string.Empty,
                                    Frames = framesList,
                                    Speed = ParseDoubleOrDefault(GetAttrAnyCase(animNode, "speed", "Speed"), 0.1),
                                    Loop = ParseBool(GetAttrAnyCase(animNode, "loop", "Loop")),
                                    Autoplay = ParseBool(GetAttrAnyCase(animNode, "autoplay", "Autoplay"))
                                });
                            }
                        }

                        scene.Elements.Add(animSprite);
                        continue;
                    }

                    if (localName == "Audio")
                    {
                        scene.Elements.Add(new AudioElement
                        {
                            Name = name,
                            Src = GetAttrAnyCase(n, "src", "Src"),
                            Loop = ParseBool(GetAttrAnyCase(n, "loop", "Loop")),
                            Autoplay = ParseBool(GetAttrAnyCase(n, "autoplay", "Autoplay")),
                            Layer = ParseIntOrDefault(GetAttrAnyCase(n, "layer", "Layer"), 0),
                            Active = ParseBoolOrDefault(GetAttrAnyCase(n, "Active", "active"), true)
                        });
                        continue;
                    }

                    if (localName == "Clickable" || localName == "ClickableArea")
                    {
                        scene.Elements.Add(new ClickableElement
                        {
                            Name = name,
                            X = ParseDouble(GetAttrAnyCase(n, "x", "X")),
                            Y = ParseDouble(GetAttrAnyCase(n, "y", "Y")),
                            Width = ParseDoubleOrDefault(GetAttrAnyCase(n, "width", "Width"), 0.2),
                            Height = ParseDoubleOrDefault(GetAttrAnyCase(n, "height", "Height"), 0.2),
                            HasClickableArea = ParseBoolOrDefault(GetAttrAnyCase(n, "hasClickableArea", "HasClickableArea"), true),
                            Layer = ParseIntOrDefault(GetAttrAnyCase(n, "layer", "Layer"), 0),
                            Active = ParseBoolOrDefault(GetAttrAnyCase(n, "Active", "active"), true)
                        });
                        continue;
                    }

                    if (localName == "Text")
                    {
                        scene.Elements.Add(new TextElement
                        {
                            Name = name,
                            X = ParseDouble(GetAttrAnyCase(n, "x", "X")),
                            Y = ParseDouble(GetAttrAnyCase(n, "y", "Y")),
                            FontSize = ParseDoubleOrDefault(GetAttrAnyCase(n, "fontSize", "FontSize"), 16),
                            FontFamily = GetAttrAnyCase(n, "fontFamily", "FontFamily"),
                            Color = GetAttrAnyCase(n, "color", "Color"),
                            Text = GetAttrAnyCase(n, "text", "Text") ?? n.InnerText?.Trim(),
                            Layer = ParseIntOrDefault(GetAttrAnyCase(n, "layer", "Layer"), 0),
                            Active = ParseBoolOrDefault(GetAttrAnyCase(n, "Active", "active"), true)
                        });
                        continue;
                    }

                    // Unknown element types are ignored for now.
                }
            }

            return scene;
        }

        private static string Escape(string value) => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string? GetAttrAnyCase(XmlNode node, string lower, string upper)
            => node.Attributes?[lower]?.Value ?? node.Attributes?[upper]?.Value;

        private static double ParseDouble(string? value)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;
            return 0;
        }

        private static double ParseDoubleOrDefault(string? value, double defaultValue)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return v;
            return defaultValue;
        }

        private static bool ParseBool(string? value)
        {
            if (bool.TryParse(value, out var b))
                return b;
            return false;
        }

        private static bool ParseBoolOrDefault(string? value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;
            if (bool.TryParse(value, out var b))
                return b;
            return defaultValue;
        }

        private static int ParseIntOrDefault(string? value, int defaultValue)
        {
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                return v;
            return defaultValue;
        }
    }
}
