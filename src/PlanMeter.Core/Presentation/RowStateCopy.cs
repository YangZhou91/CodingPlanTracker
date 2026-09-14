namespace PlanMeter.Core.Presentation;

/// <summary>
/// ROW-04..09 design-board body copy constants. Every string is an em dash (U+2014)
/// followed by an ideographic space (U+3000) before the CJK word — frozen by
/// RowStateCopyTests so a whitespace-normalizer pass cannot silently widen them.
/// No WPF types; ProviderRow assigns these to StatusText.Text.
/// </summary>
public static class RowStateCopy
{
    public const string NeedLogin = "—\u3000需要登录";
    public const string NotConfigured = "—\u3000未配置";
    public const string Loading = "—\u3000正在读取";
    public const string NoData = "—\u3000暂无数据";
    public const string RequestFailed = "—\u3000请求失败";
    public const string Unsupported = "—\u3000不支持";
    public const string UsageNotAvailable = "—\u3000无额度数据";
}
