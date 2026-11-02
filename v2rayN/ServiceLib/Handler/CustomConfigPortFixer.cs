namespace ServiceLib.Handler;

/// <summary>
/// 修正 Custom（自定义配置文件）类型的本地监听端口。
///
/// 背景：
///   v2rayN 的入站端口取自 config.Inbound[socks].LocalPort（默认 10808/本机 3128）。
///   但 Custom 类型配置的运行时生成走 CoreConfigHandler.GenerateClientCustomConfig，
///   内部只有一句 File.Copy(addressFileName, fileName) —— 原样复制，不改端口。
///   唯独 mihomo 走 CoreConfigClashService.GenerateClientCustomConfig，
///   它会把 mixed-port 改写成 GetLocalPort(socks)。
///
///   结果：除 mihomo 外的所有内核（xray / juicity / hysteria / hysteria2 /
///   naiveproxy / mieru / shadowquic）仍按配置文件里的旧端口（1080/3080/4080）监听，
///   而 v2rayN 的系统代理指向 Inbound.LocalPort → 流量进不来，
///   表现为「切换到该节点后连不上」。
///
/// 本类在复制之后按内核类型改写本地监听端口，使其与 v2rayN 的入站端口一致。
/// 修改是幂等的：已是目标端口时原样返回。
/// </summary>
public static class CustomConfigPortFixer
{
    private static readonly Regex _rxListen = new(@"^(?<prefix>.*?)(?<port>\d+)$", RegexOptions.Compiled);
    private static readonly Regex _rxNaiveListen = new(@"^(?<scheme>socks|http|redir)://(?<host>[^:]+):(?<port>\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// 按内核类型修正配置文件内容中的本地监听端口。
    /// </summary>
    /// <param name="coreType">目标内核类型</param>
    /// <param name="content">配置文件原始内容</param>
    /// <param name="port">v2rayN 的入站端口（socks）</param>
    /// <returns>修正后的内容；无法识别时返回原内容</returns>
    public static string Fix(ECoreType coreType, string content, int port)
    {
        if (port <= 0 || string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        try
        {
            return coreType switch
            {
                ECoreType.mieru => FixMieru(content, port),
                ECoreType.Xray or ECoreType.v2fly or ECoreType.v2fly_v5 => FixXray(content, port),
                ECoreType.juicity => FixListenField(content, port),
                ECoreType.hysteria or ECoreType.hysteria2 => FixSocks5Listen(content, port),
                ECoreType.naiveproxy => FixNaive(content, port),
                ECoreType.shadowquic => FixShadowquic(content, port),
                ECoreType.tuic => FixListenField(content, port),
                _ => content,
            };
        }
        catch (Exception ex)
        {
            Logging.SaveLog("CustomConfigPortFixer", ex);
            return content;
        }
    }

    /// <summary>mieru: socks5Port / rpcPort</summary>
    private static string FixMieru(string content, int port)
    {
        var node = JsonNode.Parse(content) as JsonObject;
        if (node is null)
        {
            return content;
        }
        if (node["socks5Port"] is JsonValue socks && socks.TryGetValue<int>(out var oldSocks))
        {
            if (oldSocks != port)
            {
                node["socks5Port"] = port;
            }
        }
        if (node["rpcPort"] is JsonValue rpc && rpc.TryGetValue<int>(out var oldRpc))
        {
            var newRpc = port + 1;
            if (oldRpc != newRpc)
            {
                node["rpcPort"] = newRpc;
            }
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Xray/v2ray: inbounds[].port —— socks 与 http 需错开，避免端口冲突</summary>
    private static string FixXray(string content, int port)
    {
        var node = JsonNode.Parse(content) as JsonObject;
        if (node?["inbounds"] is not JsonArray inbounds)
        {
            return content;
        }
        foreach (var item in inbounds)
        {
            if (item is not JsonObject inbound)
            {
                continue;
            }
            var proto = inbound["protocol"]?.GetValue<string>();
            var newPort = proto switch
            {
                "socks" => port,
                "http" => port + 1,
                _ => (int?)null,
            };
            if (newPort is null)
            {
                continue;
            }
            if (inbound["port"] is JsonValue pv && pv.TryGetValue<int>(out var oldPort) && oldPort != newPort.Value)
            {
                inbound["port"] = newPort.Value;
            }
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>juicity / tuic: 顶层 "listen": "127.0.0.1:1080"</summary>
    private static string FixListenField(string content, int port)
    {
        var node = JsonNode.Parse(content) as JsonObject;
        if (node is null)
        {
            return content;
        }
        if (node["listen"] is JsonValue lv && lv.TryGetValue<string>(out var listen))
        {
            var replaced = ReplacePort(listen, port);
            if (replaced != listen)
            {
                node["listen"] = replaced;
            }
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>hysteria v1/v2: socks5.listen</summary>
    private static string FixSocks5Listen(string content, int port)
    {
        var node = JsonNode.Parse(content) as JsonObject;
        if (node is null)
        {
            return content;
        }
        if (node["socks5"] is JsonObject socks5 && socks5["listen"] is JsonValue lv
            && lv.TryGetValue<string>(out var listen))
        {
            var replaced = ReplacePort(listen, port);
            if (replaced != listen)
            {
                socks5["listen"] = replaced;
            }
        }
        // 部分 hysteria 版本把 listen 放在顶层
        if (node["listen"] is JsonValue tv && tv.TryGetValue<string>(out var topListen))
        {
            var replaced = ReplacePort(topListen, port);
            if (replaced != topListen)
            {
                node["listen"] = replaced;
            }
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>naiveproxy: "listen": "socks://127.0.0.1:1080"（scheme 必须保留）</summary>
    private static string FixNaive(string content, int port)
    {
        var node = JsonNode.Parse(content) as JsonObject;
        if (node?["listen"] is not JsonValue lv || !lv.TryGetValue<string>(out var listen))
        {
            return content;
        }
        var m = _rxNaiveListen.Match(listen);
        if (!m.Success)
        {
            return content;
        }
        if (int.TryParse(m.Groups["port"].Value, out var oldPort) && oldPort != port)
        {
            node["listen"] = $"{m.Groups["scheme"].Value}://{m.Groups["host"].Value}:{port}";
        }
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>shadowquic (YAML): inbound.bind-addr: "127.0.0.1:4080"</summary>
    private static string FixShadowquic(string content, int port)
    {
        var rx = new Regex(@"(?i)(bind-addr:\s*""?[\w\.\-]+):\d+(""?)");
        return rx.Replace(content, m => $"{m.Groups[1].Value}:{port}{m.Groups[2].Value}");
    }

    /// <summary>把 "host:port" 里的端口替换掉，保留 host 部分</summary>
    private static string ReplacePort(string listen, int port)
    {
        var m = _rxListen.Match(listen);
        if (!m.Success)
        {
            return listen;
        }
        if (!int.TryParse(m.Groups["port"].Value, out var oldPort) || oldPort == port)
        {
            return listen;
        }
        return $"{m.Groups["prefix"].Value}{port}";
    }
}
