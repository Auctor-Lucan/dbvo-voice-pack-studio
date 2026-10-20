using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DbvoStudio.Core.Fomod;

/// <summary>
/// Reads and writes fomod\ModuleConfig.xml (ModConfig 5.0) and fomod\info.xml. Anything the simple editor can't
/// express (conditional installs, flags, step visibility, custom type patterns, module dependencies) is kept as raw
/// XML and written back unchanged.
/// </summary>
public static class FomodXml
{
    public const string SchemaUrl = "http://qconsulting.ca/fo3/ModConfig5.0.xsd";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    // ------------------------------------------------------------------ read

    public static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return DecodeText(bytes);
    }

    /// <summary>Decode an XML file's bytes, honouring BOMs and the declared encoding (FOMOD files are often UTF-16).</summary>
    public static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[1] == 0 && bytes[0] != 0) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes);
    }

    private static XDocument Parse(string text)
    {
        // XDocument refuses an encoding="utf-16" declaration on an already-decoded string; drop the declaration.
        text = System.Text.RegularExpressions.Regex.Replace(text.TrimStart('﻿'), @"^\s*<\?xml[^>]*\?>", "");
        return XDocument.Parse(text, LoadOptions.PreserveWhitespace);
    }

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;
    private static IEnumerable<XElement> Kids(XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name);
    private static XElement? Kid(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    private static string Raw(XElement e) => e.ToString(SaveOptions.DisableFormatting);

    public static FomodProject Read(string moduleConfigXml, string? infoXml)
    {
        var doc = Parse(moduleConfigXml);
        var config = doc.Root ?? throw new InvalidOperationException("ModuleConfig.xml is empty.");
        if (config.Name.LocalName != "config") throw new InvalidOperationException("This is not a FOMOD ModuleConfig.xml (no <config> root).");
        var p = new FomodProject();
        var mn = Kid(config, "moduleName");
        if (mn != null)
        {
            p.ModuleName = mn.Value.Trim();
            foreach (var a in mn.Attributes()) p.ModuleNameAttributes[a.Name.LocalName] = a.Value;
        }
        var mi = Kid(config, "moduleImage");
        if (mi != null) p.ModuleImage = Attr(mi, "path") ?? "";
        var md = Kid(config, "moduleDependencies");
        if (md != null) p.ModuleDependenciesXml = Raw(md);
        var req = Kid(config, "requiredInstallFiles");
        if (req != null) p.RequiredInstallFiles = ReadFiles(req);
        var steps = Kid(config, "installSteps");
        if (steps != null)
        {
            p.StepsOrder = Attr(steps, "order") ?? "Ascending";
            foreach (var se in Kids(steps, "installStep"))
            {
                var step = new FomodStep { Name = Attr(se, "name") ?? "" };
                var vis = Kid(se, "visible");
                if (vis != null) step.VisibleXml = Raw(vis);
                var ofg = Kid(se, "optionalFileGroups");
                if (ofg != null)
                {
                    step.GroupsOrder = Attr(ofg, "order") ?? "Ascending";
                    foreach (var ge in Kids(ofg, "group"))
                    {
                        var g = new FomodGroup { Name = Attr(ge, "name") ?? "", Type = Attr(ge, "type") ?? "SelectAny" };
                        var pl = Kid(ge, "plugins");
                        if (pl != null)
                        {
                            g.Order = Attr(pl, "order") ?? "Ascending";
                            foreach (var pe in Kids(pl, "plugin")) g.Options.Add(ReadOption(pe));
                        }
                        step.Groups.Add(g);
                    }
                }
                p.Steps.Add(step);
            }
        }
        var cfi = Kid(config, "conditionalFileInstalls");
        if (cfi != null) p.ConditionalFileInstallsXml = Raw(cfi);

        if (!string.IsNullOrWhiteSpace(infoXml)) p.Info = ReadInfo(infoXml);
        p.Name = !string.IsNullOrWhiteSpace(p.Info.Name) ? p.Info.Name : p.ModuleName;
        return p;
    }

    public static FomodInfo ReadInfo(string infoXml)
    {
        var info = new FomodInfo { Groups = new() };
        var root = Parse(infoXml).Root;
        if (root == null) return info;
        foreach (var e in root.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "Name": info.Name = e.Value.Trim(); break;
                case "Author": info.Author = e.Value.Trim(); break;
                case "Version": info.Version = e.Value.Trim(); break;
                case "Website": info.Website = e.Value.Trim(); break;
                case "Description": info.Description = e.Value.Trim(); break;
                case "Groups": info.Groups = Kids(e, "element").Select(x => x.Value.Trim()).ToList(); break;
                default: info.ExtraXml.Add(Raw(e)); break;
            }
        }
        return info;
    }

    private static List<FomodFile> ReadFiles(XElement list) =>
        list.Elements().Where(e => e.Name.LocalName is "file" or "folder").Select(e => new FomodFile
        {
            Kind = e.Name.LocalName,
            Source = Attr(e, "source") ?? "",
            Destination = Attr(e, "destination") ?? "",
            Priority = int.TryParse(Attr(e, "priority"), out var pr) ? pr : 0,
            AlwaysInstall = bool.TryParse(Attr(e, "alwaysInstall"), out var ai) ? ai : null,
            InstallIfUsable = bool.TryParse(Attr(e, "installIfUsable"), out var iu) ? iu : null,
        }).ToList();

    private static FomodOption ReadOption(XElement pe)
    {
        var o = new FomodOption { Name = Attr(pe, "name") ?? "" };
        o.Description = Kid(pe, "description")?.Value ?? "";
        var img = Kid(pe, "image");
        if (img != null) o.Image = Attr(img, "path") ?? "";
        var files = Kid(pe, "files");
        if (files != null) o.Files = ReadFiles(files);
        var flags = Kid(pe, "conditionFlags");
        if (flags != null)
        {
            o.ConditionFlagsXml = Raw(flags);
            o.FlagsFirst = files == null || flags.ElementsBeforeSelf().Contains(files) == false;
        }
        var td = Kid(pe, "typeDescriptor");
        ReadDetection(o, td);
        return o;
    }

    /// <summary>Recognise the simple detection shapes; anything else is kept as Advanced.</summary>
    private static void ReadDetection(FomodOption o, XElement? td)
    {
        o.Plugins = new();
        if (td == null) { o.Mode = DetectMode.Optional; return; }
        var type = Kid(td, "type");
        var dt = Kid(td, "dependencyType");
        if (type != null && dt == null)
        {
            switch (Attr(type, "name"))
            {
                case "Required": o.Mode = DetectMode.Required; return;
                case "Optional": o.Mode = DetectMode.Optional; return;
                case "Recommended": o.Mode = DetectMode.Recommended; return;
            }
        }
        if (dt != null && TryReadDetect(dt, o)) { o.Mode = DetectMode.Detect; return; }
        o.Mode = DetectMode.Advanced;
        o.TypeDescriptorXml = Raw(td);
    }

    private static bool TryReadDetect(XElement dt, FomodOption o)
    {
        var def = Kid(dt, "defaultType");
        if (def == null || Attr(def, "name") != "Optional") return false;
        var patterns = Kid(dt, "patterns");
        if (patterns == null) return false;
        var list = Kids(patterns, "pattern").ToList();
        if (list.Count is < 1 or > 2) return false;

        (string op, List<string> files, string state, string type)? Shape(XElement pat)
        {
            var deps = Kid(pat, "dependencies");
            var ty = Kid(pat, "type");
            if (deps == null || ty == null) return null;
            if (deps.Elements().Any(e => e.Name.LocalName != "fileDependency")) return null;
            var fds = Kids(deps, "fileDependency").ToList();
            if (fds.Count == 0) return null;
            var states = fds.Select(f => Attr(f, "state")).Distinct().ToList();
            if (states.Count != 1) return null;
            return (Attr(deps, "operator") ?? "And", fds.Select(f => Attr(f, "file") ?? "").ToList(), states[0]!, Attr(ty, "name") ?? "");
        }

        var a = Shape(list[0]);
        if (a == null || a.Value.state != "Active" || a.Value.type != "Recommended") return false;
        bool all = a.Value.op == "And";
        if (list.Count == 2)
        {
            var b = Shape(list[1]);
            if (b == null || b.Value.state != "Inactive" || b.Value.type != "NotUsable") return false;
            if (!b.Value.files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).SequenceEqual(a.Value.files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) return false;
            // Any: Or(Active) + And(Inactive). All: And(Active) + Or(Inactive). One plugin: both operators are equivalent.
            bool consistent = a.Value.files.Count == 1 || (all ? b.Value.op == "Or" : b.Value.op == "And");
            if (!consistent) return false;
            o.NotUsableWhenMissing = true;
        }
        else o.NotUsableWhenMissing = false;
        o.RequireAll = all && a.Value.files.Count > 1;
        o.Plugins = a.Value.files;
        return true;
    }

    // ------------------------------------------------------------------ write

    /// <summary>Build ModuleConfig.xml. <paramref name="mapSource"/> rewrites file sources (used when packaging).</summary>
    public static string WriteModuleConfig(FomodProject p, Func<string, string>? mapSource = null, Func<string, string>? mapImage = null)
    {
        mapSource ??= s => s;
        mapImage ??= s => s;
        var config = new XElement("config",
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
            new XAttribute(Xsi + "noNamespaceSchemaLocation", SchemaUrl));
        var mn = new XElement("moduleName", string.IsNullOrWhiteSpace(p.ModuleName) ? p.Info.Name : p.ModuleName);
        foreach (var kv in p.ModuleNameAttributes) mn.SetAttributeValue(kv.Key, kv.Value);
        config.Add(mn);
        if (!string.IsNullOrWhiteSpace(p.ModuleImage)) config.Add(new XElement("moduleImage", new XAttribute("path", mapImage(p.ModuleImage))));
        if (!string.IsNullOrWhiteSpace(p.ModuleDependenciesXml)) config.Add(XElement.Parse(p.ModuleDependenciesXml));
        if (p.RequiredInstallFiles.Count > 0) config.Add(FilesElement("requiredInstallFiles", p.RequiredInstallFiles, mapSource));
        var steps = p.Steps.Where(s => s.Groups.Any(g => g.Options.Count > 0)).ToList();
        if (steps.Count > 0)
        {
            var se = new XElement("installSteps", new XAttribute("order", p.StepsOrder));
            foreach (var step in steps)
            {
                var st = new XElement("installStep", new XAttribute("name", step.Name));
                if (!string.IsNullOrWhiteSpace(step.VisibleXml)) st.Add(XElement.Parse(step.VisibleXml));
                var groups = new XElement("optionalFileGroups", new XAttribute("order", step.GroupsOrder));
                foreach (var g in step.Groups.Where(g => g.Options.Count > 0))
                {
                    var plugins = new XElement("plugins", new XAttribute("order", g.Order));
                    foreach (var o in g.Options) plugins.Add(OptionElement(o, mapSource, mapImage));
                    groups.Add(new XElement("group", new XAttribute("name", g.Name), new XAttribute("type", g.Type), plugins));
                }
                st.Add(groups);
                se.Add(st);
            }
            config.Add(se);
        }
        if (!string.IsNullOrWhiteSpace(p.ConditionalFileInstallsXml)) config.Add(XElement.Parse(p.ConditionalFileInstallsXml));
        return Serialize(new XDocument(config), p.Encoding);
    }

    private static XElement FilesElement(string name, IEnumerable<FomodFile> files, Func<string, string> mapSource)
    {
        var el = new XElement(name);
        foreach (var f in files)
        {
            var e = new XElement(f.Kind == "file" ? "file" : "folder",
                new XAttribute("source", mapSource(f.Source)),
                new XAttribute("destination", f.Destination));
            if (f.AlwaysInstall != null) e.SetAttributeValue("alwaysInstall", f.AlwaysInstall.Value ? "true" : "false");
            if (f.InstallIfUsable != null) e.SetAttributeValue("installIfUsable", f.InstallIfUsable.Value ? "true" : "false");
            e.SetAttributeValue("priority", f.Priority);
            el.Add(e);
        }
        return el;
    }

    private static XElement OptionElement(FomodOption o, Func<string, string> mapSource, Func<string, string> mapImage)
    {
        var e = new XElement("plugin", new XAttribute("name", o.Name), new XElement("description", o.Description ?? ""));
        if (!string.IsNullOrWhiteSpace(o.Image)) e.Add(new XElement("image", new XAttribute("path", mapImage(o.Image))));
        var flags = string.IsNullOrWhiteSpace(o.ConditionFlagsXml) ? null : XElement.Parse(o.ConditionFlagsXml);
        var files = o.Files.Count > 0 ? FilesElement("files", o.Files, mapSource) : null;
        // The schema needs files or conditionFlags; an information-only option gets an empty <files/>.
        if (flags == null && files == null) files = new XElement("files");
        if (flags != null && o.FlagsFirst) { e.Add(flags); if (files != null) e.Add(files); }
        else { if (files != null) e.Add(files); if (flags != null) e.Add(flags); }
        e.Add(TypeDescriptor(o));
        return e;
    }

    public static XElement TypeDescriptor(FomodOption o)
    {
        switch (o.Mode)
        {
            case DetectMode.Advanced when !string.IsNullOrWhiteSpace(o.TypeDescriptorXml):
                return XElement.Parse(o.TypeDescriptorXml);
            case DetectMode.Required:
                return new XElement("typeDescriptor", new XElement("type", new XAttribute("name", "Required")));
            case DetectMode.Recommended:
                return new XElement("typeDescriptor", new XElement("type", new XAttribute("name", "Recommended")));
            case DetectMode.Detect when o.Plugins.Any(x => !string.IsNullOrWhiteSpace(x)):
            {
                var plugins = o.Plugins.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                bool all = o.RequireAll || plugins.Count == 1;
                XElement Deps(string op, string state) => new("dependencies", new XAttribute("operator", op),
                    plugins.Select(f => new XElement("fileDependency", new XAttribute("file", f), new XAttribute("state", state))));
                var patterns = new XElement("patterns",
                    new XElement("pattern", Deps(all ? "And" : "Or", "Active"), new XElement("type", new XAttribute("name", "Recommended"))));
                if (o.NotUsableWhenMissing)
                    patterns.Add(new XElement("pattern", Deps(all && plugins.Count > 1 ? "Or" : "And", "Inactive"), new XElement("type", new XAttribute("name", "NotUsable"))));
                return new XElement("typeDescriptor", new XElement("dependencyType",
                    new XElement("defaultType", new XAttribute("name", "Optional")), patterns));
            }
            default:
                return new XElement("typeDescriptor", new XElement("type", new XAttribute("name", "Optional")));
        }
    }

    public static string WriteInfo(FomodProject p)
    {
        var i = p.Info;
        var root = new XElement("fomod", new XElement("Name", string.IsNullOrWhiteSpace(i.Name) ? p.Name : i.Name));
        if (!string.IsNullOrWhiteSpace(i.Author)) root.Add(new XElement("Author", i.Author));
        if (!string.IsNullOrWhiteSpace(i.Version)) root.Add(new XElement("Version", i.Version));
        if (!string.IsNullOrWhiteSpace(i.Website)) root.Add(new XElement("Website", i.Website));
        if (!string.IsNullOrWhiteSpace(i.Description)) root.Add(new XElement("Description", i.Description));
        if (i.Groups.Count > 0) root.Add(new XElement("Groups", i.Groups.Select(g => new XElement("element", g))));
        foreach (var x in i.ExtraXml) root.Add(XElement.Parse(x));
        return Serialize(new XDocument(root), p.Encoding);
    }

    private static string Serialize(XDocument doc, string encoding)
    {
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "\t", OmitXmlDeclaration = true, NewLineChars = "\r\n" };
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, settings)) doc.Save(w);
        var enc = encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ? "utf-8" : "utf-16";
        return $"<?xml version=\"1.0\" encoding=\"{enc}\"?>\r\n" + sb;
    }

    /// <summary>Bytes for a written XML string, with the BOM matching its declaration.</summary>
    public static byte[] Encode(string xml, string encoding)
    {
        var enc = encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase) ? (Encoding)new UTF8Encoding(true) : new UnicodeEncoding(false, true);
        var pre = enc.GetPreamble();
        var body = enc.GetBytes(xml);
        var all = new byte[pre.Length + body.Length];
        pre.CopyTo(all, 0);
        body.CopyTo(all, pre.Length);
        return all;
    }
}
