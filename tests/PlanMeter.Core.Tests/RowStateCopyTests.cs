using FluentAssertions;
using PlanMeter.Core.Presentation;
using Xunit;

namespace PlanMeter.Core.Tests;

/// <summary>
/// ROW-04..09 design-board copy freeze. Every constant uses an em dash (U+2014)
/// followed by an ideographic space (U+3000) before the CJK word — a later
/// "normalize whitespace" pass must not silently widen or narrow these.
/// </summary>
public sealed class RowStateCopyTests
{
    [Fact]
    public void NeedLogin_is_em_dash_ideographic_space_need_login()
    {
        RowStateCopy.NeedLogin.Should().Be("—　需要登录");
        // Freeze the rune between the em dash and the CJK word.
        RowStateCopy.NeedLogin.Should().Contain("—\u3000需要登录");
    }

    [Fact]
    public void NotConfigured_is_em_dash_ideographic_space_not_configured()
    {
        RowStateCopy.NotConfigured.Should().Be("—　未配置");
        RowStateCopy.NotConfigured.Should().Contain("—\u3000未配置");
    }

    [Fact]
    public void Loading_is_em_dash_ideographic_space_loading()
    {
        RowStateCopy.Loading.Should().Be("—　正在读取");
        RowStateCopy.Loading.Should().Contain("—\u3000正在读取");
    }

    [Fact]
    public void NoData_is_em_dash_ideographic_space_no_data()
    {
        RowStateCopy.NoData.Should().Be("—　暂无数据");
        RowStateCopy.NoData.Should().Contain("—\u3000暂无数据");
    }

    [Fact]
    public void RequestFailed_is_em_dash_ideographic_space_request_failed()
    {
        RowStateCopy.RequestFailed.Should().Be("—　请求失败");
        RowStateCopy.RequestFailed.Should().Contain("—\u3000请求失败");
    }

    [Fact]
    public void Unsupported_is_em_dash_ideographic_space_unsupported()
    {
        RowStateCopy.Unsupported.Should().Be("—　不支持");
        RowStateCopy.Unsupported.Should().Contain("—\u3000不支持");
    }

    [Fact]
    public void UsageNotAvailable_is_em_dash_ideographic_space_usage_not_available()
    {
        RowStateCopy.UsageNotAvailable.Should().Be("—　无额度数据");
        RowStateCopy.UsageNotAvailable.Should().Contain("—\u3000无额度数据");
    }
}
