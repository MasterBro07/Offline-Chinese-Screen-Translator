using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

public class RegionMergerTests
{
    private static TextRegion Line(string text, int x, int y, int width, int height = 20) =>
        new(text, new ScreenRect(x, y, width, height));

    private static IReadOnlyList<TextRegion> Merge(params TextRegion[] lines) => RegionMerger.MergeLines(lines);

    [Fact]
    public void Empty_And_Single()
    {
        Assert.Empty(Merge());
        var only = Line("你好", 5, 5, 40);
        Assert.Same(only, Assert.Single(Merge(only)));
        Assert.Throws<ArgumentNullException>(() => RegionMerger.MergeLines(null!));
    }

    [Fact]
    public void WrappedChineseParagraph_IsMergedWithoutSeparator()
    {
        var merged = Assert.Single(Merge(
            Line("这款手机壳采用优质硅胶材料制作而成", 10, 100, 400),
            Line("手感柔软舒适并且不易沾染指纹", 10, 124, 380)));

        Assert.Equal("这款手机壳采用优质硅胶材料制作而成手感柔软舒适并且不易沾染指纹", merged.Text);
        Assert.Equal(new ScreenRect(10, 100, 400, 44), merged.Bounds);
    }

    [Fact]
    public void WrappedLatinParagraph_IsMergedWithSpace()
    {
        var merged = Assert.Single(Merge(
            Line("This phone case is made of soft", 0, 0, 400),
            Line("silicone and feels great", 0, 24, 300)));

        Assert.Equal("This phone case is made of soft silicone and feels great", merged.Text);
    }

    [Fact]
    public void MixedScripts_GetASpaceUnlessBothSidesAreCjk()
    {
        Assert.Equal("全新正品保证支持 iPhone 15 Pro Max 手机壳",
            Assert.Single(Merge(
                Line("全新正品保证支持 iPhone 15 Pro", 0, 0, 400),
                Line("Max 手机壳", 0, 24, 150))).Text);

        Assert.Equal("限时优惠活动全场商品满 100 元包邮",
            Assert.Single(Merge(
                Line("限时优惠活动全场商品满 100", 0, 0, 400),
                Line("元包邮", 0, 24, 80))).Text);
    }

    [Fact]
    public void ThreeLineParagraph_EndsAtTheShortLastLine()
    {
        var result = Merge(
            Line("第一行文字内容很长很长很长很长", 0, 0, 400),
            Line("第二行文字内容也很长很长很长", 0, 24, 390),
            Line("第三行短", 0, 48, 100),
            Line("另一个段落的标题文字", 0, 72, 260));

        Assert.Equal(2, result.Count);
        Assert.Equal("第一行文字内容很长很长很长很长第二行文字内容也很长很长很长第三行短", result[0].Text);
        Assert.Equal(new ScreenRect(0, 0, 400, 68), result[0].Bounds);
        Assert.Equal("另一个段落的标题文字", result[1].Text);
    }

    [Fact]
    public void StackedShortLabels_StaySeparate()
    {
        var result = Merge(
            Line("设置", 0, 0, 40),
            Line("消息", 0, 24, 40),
            Line("订单", 0, 48, 40),
            Line("我的收藏", 0, 72, 80));

        Assert.Equal(new[] { "设置", "消息", "订单", "我的收藏" }, result.Select(r => r.Text));
    }

    [Fact]
    public void ShortUpperLine_AboveALongOne_StaysSeparate()
    {
        // A heading above a paragraph: the upper line is not "full".
        var result = Merge(
            Line("商品详情介绍说明", 0, 0, 200),
            Line("这款手机壳采用优质硅胶材料制作而成", 0, 24, 400));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DifferentFontSizes_StaySeparate()
    {
        var result = Merge(
            Line("这款手机壳采用优质硅胶材料制作而成", 0, 0, 400, height: 30),
            Line("手感柔软舒适并且不易沾染指纹", 0, 34, 380, height: 20));

        Assert.Equal(2, result.Count);
    }

    [Theory]
    [InlineData(34, false)] // gap 14 = 0.7 line heights: too far
    [InlineData(33, true)]
    [InlineData(20, true)]  // touching
    [InlineData(15, true)]  // slight overlap
    [InlineData(10, false)] // overlapping too much: same row, not the next line
    public void VerticalGap_DecidesWhetherLinesBelongTogether(int lowerY, bool merged)
    {
        var result = Merge(
            Line("这款手机壳采用优质硅胶材料制作而成", 0, 0, 400),
            Line("手感柔软舒适并且不易沾染指纹", 0, lowerY, 380));

        Assert.Equal(merged ? 1 : 2, result.Count);
    }

    [Fact]
    public void DifferentColumns_StaySeparate()
    {
        var result = Merge(
            Line("这款手机壳采用优质硅胶材料制作而成", 0, 0, 400),
            Line("手感柔软舒适并且不易沾染指纹", 450, 24, 380));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void IndentedButWithinOneLineHeight_IsMerged()
    {
        Assert.Single(Merge(
            Line("这款手机壳采用优质硅胶材料制作而成", 20, 0, 380),
            Line("手感柔软舒适并且不易沾染指纹", 0, 24, 380)));
    }

    [Fact]
    public void CentredLines_WithStrongOverlap_AreMerged()
    {
        var merged = Assert.Single(Merge(
            Line("欢迎来到我们的官方旗舰店铺", 50, 0, 300),
            Line("全场商品限时包邮", 100, 24, 200)));

        Assert.Equal(new ScreenRect(50, 0, 300, 44), merged.Bounds);
    }

    [Fact]
    public void SentenceEnd_IsNotMerged()
    {
        var result = Merge(
            Line("这款手机壳采用优质硅胶材料制作而成。", 0, 0, 400),
            Line("手感柔软舒适并且不易沾染指纹", 0, 24, 380));

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void LineEndingInComma_ContinuesEvenWhenShort()
    {
        var merged = Assert.Single(Merge(
            Line("亲，", 0, 0, 40),
            Line("您的订单已经发货请注意查收", 0, 24, 300)));

        Assert.Equal("亲，您的订单已经发货请注意查收", merged.Text);

        // ...but only when the left edges line up.
        Assert.Equal(2, Merge(
            Line("亲，", 15, 0, 40),
            Line("您的订单已经发货请注意查收", 0, 24, 300)).Count);
    }

    [Fact]
    public void TwoColumns_AreMergedIndependently()
    {
        var result = Merge(
            Line("左边第一行文字内容很长很长", 0, 0, 300),
            Line("右边第一行文字内容很长很长", 400, 0, 300),
            Line("左边第二行", 0, 24, 120),
            Line("右边第二行", 400, 24, 120));

        Assert.Equal(new[] { "左边第一行文字内容很长很长左边第二行", "右边第一行文字内容很长很长右边第二行" },
            result.Select(r => r.Text));
    }

    [Fact]
    public void Output_IsOrderedTopToBottomThenLeftToRight_WhateverTheInputOrder()
    {
        var a = Line("甲", 300, 0, 20);
        var b = Line("乙", 0, 0, 20);
        var c = Line("丙", 0, 200, 20);
        var d = Line("丁", 100, 100, 20);
        var expected = new[] { "乙", "甲", "丁", "丙" };

        Assert.Equal(expected, Merge(a, b, c, d).Select(r => r.Text));
        Assert.Equal(expected, Merge(d, c, b, a).Select(r => r.Text));
        Assert.Equal(expected, Merge(c, a, d, b).Select(r => r.Text));
    }

    [Fact]
    public void EqualPositions_KeepInputOrder()
    {
        var result = Merge(Line("一", 0, 0, 20), Line("二", 0, 0, 20), Line("三", 0, 0, 20));
        Assert.Equal(new[] { "一", "二", "三" }, result.Select(r => r.Text));
    }

    [Fact]
    public void DegenerateRegions_ArePassedThrough()
    {
        var result = Merge(
            Line("这款手机壳采用优质硅胶材料制作而成", 0, 0, 400, height: 0),
            Line("   ", 0, 2, 400),
            Line("手感柔软舒适并且不易沾染指纹", 0, 24, 380));

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void TextIsTrimmedWhenJoining()
    {
        var merged = Assert.Single(Merge(
            Line(" 这款手机壳采用优质硅胶材料制作而成 ", 0, 0, 400),
            Line(" 手感柔软舒适 ", 0, 24, 200)));

        Assert.Equal("这款手机壳采用优质硅胶材料制作而成手感柔软舒适", merged.Text);
    }
}

public class TranslationPipelineMergeTests
{
    private static readonly TextRegion Upper = new("这款手机壳采用优质硅胶材料制作而成", new ScreenRect(0, 0, 400, 20));
    private static readonly TextRegion Lower = new("手感柔软舒适", new ScreenRect(0, 24, 150, 20));
    private static readonly TextRegion Latin = new("Hello world, this is a long line", new ScreenRect(0, 48, 400, 20));

    [Fact]
    public void MergeLines_DefaultsToTrue() => Assert.True(new PipelineOptions().MergeLines);

    [Fact]
    public async Task MergesParagraphLines_AfterTheHanFilter()
    {
        var source = new FakeTextSource();
        var translator = new FakeTranslator();
        var presenter = new FakePresenter();
        await using var pipeline = new TranslationPipeline(source, translator, presenter, new PipelineOptions());
        source.Enqueue(Upper, Lower, Latin);

        Assert.Equal(1, await pipeline.RunOnceAsync(default));

        Assert.Equal(new[] { "这款手机壳采用优质硅胶材料制作而成手感柔软舒适" }, Assert.Single(translator.Calls));
        var shown = Assert.Single(Assert.Single(presenter.Shown));
        Assert.Equal(new ScreenRect(0, 0, 400, 44), shown.Bounds);
    }

    [Fact]
    public async Task MergeLinesOff_KeepsLinesApart()
    {
        var source = new FakeTextSource();
        var translator = new FakeTranslator();
        var presenter = new FakePresenter();
        await using var pipeline = new TranslationPipeline(source, translator, presenter,
            new PipelineOptions { MergeLines = false });
        source.Enqueue(Upper, Lower, Latin);

        Assert.Equal(2, await pipeline.RunOnceAsync(default));

        Assert.Equal(new[] { Upper.Text, Lower.Text }, Assert.Single(translator.Calls));
    }
}
