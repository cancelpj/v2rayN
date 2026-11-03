using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ServiceLib.Common;

public class YamlUtils
{
    private static readonly string _tag = "YamlUtils";

    #region YAML

    /// <summary>
    /// Deserialize
    /// </summary>
    /// <param name="str"></param>
    /// <returns></returns>
    public static YamlMappingNode FromYaml(string str)
    {
        //var deserializer = new DeserializerBuilder()
        //    .WithNamingConvention(PascalCaseNamingConvention.Instance)
        //    .Build();
        //try
        //{
        //    var obj = deserializer.Deserialize<T>(str);
        //    return obj;
        //}
        //catch (Exception ex)
        //{
        //    Logging.SaveLog(_tag, ex);
        //    return deserializer.Deserialize<T>("");
        //}
        try
        {
            return ParseYaml(str);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        // 2026-11-03：部分机场的 clash 订阅源文件把整份配置写了两遍，
        // 顶层键重复会让 YamlStream.Load 抛 "Duplicate key xxx"，
        // 整份配置作废 → 生成的 clash 配置丢失 proxy-groups → 内核解析失败。
        // 这里做一次兜底：折叠重复的顶层键（保留后者，与旧版 Deserializer 语义一致）后重试。
        try
        {
            var fixedText = YamlDuplicateKeyFixer.Fix(str);
            if (fixedText.IsNotEmpty())
            {
                var node = ParseYaml(fixedText);
                Logging.SaveLog("YamlUtils.FromYaml: 顶层重复键已折叠后重试解析成功");
                return node;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"{_tag}-DuplicateKeyFixer", ex);
        }

        return new YamlMappingNode();
    }

    private static YamlMappingNode ParseYaml(string str)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(str);
        yaml.Load(reader);
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    /// <summary>
    /// Serialize
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    public static string ToYaml(YamlMappingNode? node)
    {
        var result = string.Empty;
        if (node == null)
        {
            return result;
        }
        try
        {
            var doc = new YamlDocument(node);
            var stream = new YamlStream(doc);
            using var writer = new StringWriter();
            stream.Save(writer, assignAnchors: false);
            result = writer.ToString();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        return result;
    }

    public static string? PreprocessYaml(string str)
    {
        try
        {
            using var reader = new StringReader(str);
            var parser = new Parser(reader);
            var mergingParser = new MergingParser(parser);
            var yamlStream = new YamlStream();
            yamlStream.Load(mergingParser);
            using var writer = new StringWriter();
            yamlStream.Save(writer, assignAnchors: false);

            return writer.ToString();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return null;
        }
    }

    #endregion YAML
}
