namespace ServiceLib.Handler;

/// <summary>
/// 修复 YAML 顶层重复键
/// ======================
/// 背景（2026-11-03 实测）：
///   部分机场的 clash 订阅源文件本身就把整份配置写了两遍，
///   例如 quick/2/config.yaml 有 15 个顶层键（= 7 + 7 + 1）：
///     secret / mixed-port / allow-lan / log-level / dns / proxies / proxy-groups
///     重复第二遍 …… 末尾再跟一个 rules
///
///   两份内容并不相同——第二份是超集，多出
///     🛑 全球拦截 / 🍃 应用净化 / 🐟 漏网之鱼 三个 proxy-group。
///
///   v2rayN 旧版 YamlUtils 用 DeserializerBuilder 解析，重复键「后者覆盖前者」，
///   实际生效的就是第二份（10 个组），恰好与本机 Mixin.yaml 里
///   prepend-rules / append-rules 引用的组名对得上，所以一直正常。
///
///   上游新版把 YamlUtils 换成 YamlStream.Load：
///     YamlException: Duplicate key secret
///   异常被 catch 后返回空的 YamlMappingNode → 整份配置作废 →
///   生成的 mihomo 配置丢失 proxy-groups → rules 引用了不存在的组
///   → mihomo 解析失败秒退：
///     Parse config error: rules[7] [DOMAIN-KEYWORD,ad,🛑 全球拦截]
///     error: proxy [🛑 全球拦截] not found
///
/// 修法：
///   在文本层面把「重复出现的顶层键」折叠成一份，保留【后者】（与旧版语义一致），
///   再交给 YamlStream.Load 正常解析。
///
/// 判定「重复」的方式（不依赖 YAML 库，避免自己也抛异常）：
///   1. 逐行扫描，记录所有顶层键（行首无缩进、形如 key: ）及其所在行号；
///   2. 若某个顶层键出现多次，则这些位置构成若干个「同名块」；
///   3. 把同名块中【最后一个】保留，其余整块删除。
///   块的范围 = 从该键所在行到「下一个顶层键所在行之前」的所有行。
///
/// 注意事项：
///   - 只处理顶层键，嵌套层级（缩进 > 0）一律不动；
///   - 列表项（以 "- " 开头）不算顶层键；
///   - 块内若含其它未重复的顶层键（异常情况），保守放弃改写，原样返回。
/// </summary>
public static class YamlDuplicateKeyFixer
{
    /// <summary>
    /// 若文本含重复顶层键，返回折叠后（保留后者）的新文本；否则返回 null。
    /// </summary>
    public static string? Fix(string text)
    {
        if (text.IsNullOrEmpty())
        {
            return null;
        }

        var lines = text.Split('\n');
        // 记录顶层键 -> 出现过的行号列表
        var keyPositions = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var keyOrder = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var key = MatchTopKey(lines[i]);
            if (key == null)
            {
                continue;
            }
            if (!keyPositions.TryGetValue(key, out var list))
            {
                list = new List<int>();
                keyPositions[key] = list;
                keyOrder.Add(key);
            }
            list.Add(i);
        }

        var duplicated = keyOrder.Where(k => keyPositions[k].Count > 1).ToList();
        if (duplicated.Count == 0)
        {
            return null; // 无重复，无需处理
        }

        // 计算每个顶层键所在块的结束行（下一个顶层键所在行）
        var allKeyLines = new List<int>();
        foreach (var k in keyOrder)
        {
            allKeyLines.AddRange(keyPositions[k]);
        }
        allKeyLines.Sort();

        var blockEnd = new Dictionary<int, int>();
        for (int idx = 0; idx < allKeyLines.Count; idx++)
        {
            int start = allKeyLines[idx];
            int end = (idx + 1 < allKeyLines.Count) ? allKeyLines[idx + 1] : lines.Length;
            blockEnd[start] = end;
        }

        // 需要删除的块：同名键的所有【非最后一次】出现
        var removeLines = new SortedSet<int>();
        foreach (var k in duplicated)
        {
            var positions = keyPositions[k];
            for (int i = 0; i < positions.Count - 1; i++)
            {
                int start = positions[i];
                int end = blockEnd[start];
                // 保守检查：块内如果出现了其它未重复的顶层键，说明结构不规整，放弃
                for (int ln = start + 1; ln < end; ln++)
                {
                    var inner = MatchTopKey(lines[ln]);
                    if (inner != null && inner != k)
                    {
                        return null; // 结构异常，整体放弃改写
                    }
                }
                for (int ln = start; ln < end; ln++)
                {
                    removeLines.Add(ln);
                }
            }
        }

        if (removeLines.Count == 0)
        {
            return null;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            if (removeLines.Contains(i))
            {
                continue;
            }
            sb.Append(lines[i]);
            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 若该行是一个顶层键（无缩进、形如 key: ），返回 key；否则返回 null。
    /// </summary>
    private static string? MatchTopKey(string line)
    {
        if (line.Length == 0)
        {
            return null;
        }
        // 有缩进 -> 嵌套层级，不是顶层键
        if (line[0] == ' ' || line[0] == '\t')
        {
            return null;
        }
        // 列表项
        if (line[0] == '-')
        {
            return null;
        }
        int colon = line.IndexOf(':');
        if (colon <= 0)
        {
            return null;
        }
        var key = line[..colon].Trim();
        if (key.Length == 0)
        {
            return null;
        }
        // key 只能由字母/数字/下划线/连字符组成
        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
            {
                return null;
            }
        }
        return key;
    }
}
