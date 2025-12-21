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

        public abstract string ElementType { get; }

        public virtual string DisplayName => $"{ElementType}: {Name}";
    }

    public sealed class SpriteElement : BaseElement
    {
        public override string ElementType => "Sprite";
        public string? ImageSrc { get; set; }
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
            sb.AppendLine($"<Scene Name=\"{Escape(scene.Name)}\">");
            sb.AppendLine($"  <Camera Name=\"{Escape(scene.Camera.Name)}\" X=\"{scene.Camera.X}\" Y=\"{scene.Camera.Y}\" Zoom=\"{scene.Camera.Zoom}\" Width=\"{scene.Camera.Width}\" Height=\"{scene.Camera.Height}\" />");
            sb.AppendLine("  <Elements>");

            foreach (var el in scene.Elements)
            {
                switch (el)
                {
                    case SpriteElement se:
                        sb.AppendLine($"    <Sprite Name=\"{Escape(se.Name)}\" X=\"{se.X}\" Y=\"{se.Y}\" Width=\"{se.Width}\" Height=\"{se.Height}\" ImageSrc=\"{Escape(se.ImageSrc ?? string.Empty)}\" />");
                        break;
                    case AudioElement ae:
                        sb.AppendLine($"    <Audio Name=\"{Escape(ae.Name)}\" Src=\"{Escape(ae.Src ?? string.Empty)}\" Loop=\"{ae.Loop}\" Autoplay=\"{ae.Autoplay}\" />");
                        break;
                    case ClickableElement ce:
                        sb.AppendLine($"    <Clickable Name=\"{Escape(ce.Name)}\" X=\"{ce.X}\" Y=\"{ce.Y}\" Width=\"{ce.Width}\" Height=\"{ce.Height}\" HasClickableArea=\"{ce.HasClickableArea}\" />");
                        break;
                    case TextElement te:
                        sb.AppendLine($"    <Text Name=\"{Escape(te.Name)}\" Text=\"{Escape(te.Text ?? string.Empty)}\" X=\"{te.X}\" Y=\"{te.Y}\" FontSize=\"{te.FontSize}\" FontFamily=\"{Escape(te.FontFamily ?? string.Empty)}\" Color=\"{Escape(te.Color ?? string.Empty)}\" />");
                        break;
                    default:
                        sb.AppendLine($"    <Element Type=\"{Escape(el.ElementType)}\" Name=\"{Escape(el.Name)}\" />");
                        break;
                }
            }

            sb.AppendLine("  </Elements>");
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
            var elementNodes = sceneNode.SelectNodes("./Sprite|./Audio|./Clickable|./Text|./Element")
                           ?? sceneNode.SelectNodes("./Elements/*");

            // If both exist, prefer direct children (engine format)
            if (sceneNode.SelectNodes("./Sprite|./Audio|./Clickable|./Text|./Element") is XmlNodeList direct && direct.Count > 0)
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
                            ImageSrc = GetAttrAnyCase(n, "imageSrc", "ImageSrc")
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
                                HasClickableArea = true
                            });
                        }

                        continue;
                    }

                    if (localName == "Audio")
                    {
                        scene.Elements.Add(new AudioElement
                        {
                            Name = name,
                            Src = GetAttrAnyCase(n, "src", "Src"),
                            Loop = ParseBool(GetAttrAnyCase(n, "loop", "Loop")),
                            Autoplay = ParseBool(GetAttrAnyCase(n, "autoplay", "Autoplay"))
                        });
                        continue;
                    }

                    if (localName == "Clickable")
                    {
                        scene.Elements.Add(new ClickableElement
                        {
                            Name = name,
                            X = ParseDouble(GetAttrAnyCase(n, "x", "X")),
                            Y = ParseDouble(GetAttrAnyCase(n, "y", "Y")),
                            Width = ParseDoubleOrDefault(GetAttrAnyCase(n, "width", "Width"), 0.2),
                            Height = ParseDoubleOrDefault(GetAttrAnyCase(n, "height", "Height"), 0.2),
                            HasClickableArea = ParseBool(GetAttrAnyCase(n, "hasClickableArea", "HasClickableArea"))
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
                            Text = GetAttrAnyCase(n, "text", "Text") ?? n.InnerText?.Trim()
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
    }
}
