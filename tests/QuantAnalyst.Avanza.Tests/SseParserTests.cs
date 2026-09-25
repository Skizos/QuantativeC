using QuantAnalyst.Avanza.Streaming;

namespace QuantAnalyst.Avanza.Tests;

/// <summary>WHATWG event-stream interpretation (https://html.spec.whatwg.org/multipage/server-sent-events.html).</summary>
public sealed class SseParserTests
{
    private static List<SseEvent> Parse(string text, SseParser? parser = null, int chunk = int.MaxValue)
    {
        parser ??= new SseParser();
        var events = new List<SseEvent>();
        for (int i = 0; i < text.Length; i += chunk)
        {
            parser.Feed(text.AsSpan(i, Math.Min(chunk, text.Length - i)), events);
        }

        return events;
    }

    [Fact]
    public void FieldsIdRetryAndDefaultEventName()
    {
        List<SseEvent> events = Parse("id: e1\nevent: ORDER_DEPTH\nretry: 1000\ndata: {\"a\":1}\n\ndata: plain\n\n");
        Assert.Equal(
            [new SseEvent("ORDER_DEPTH", "{\"a\":1}", "e1", 1000), new SseEvent("message", "plain", "e1", null)],
            events);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void AllLineEndings_AlsoSplitAcrossChunks(string eol)
    {
        string text = $"event: info{eol}data: a{eol}data: b{eol}{eol}: comment{eol}data:  two spaces{eol}{eol}";
        var expected = new List<SseEvent> { new("info", "a\nb", string.Empty, null), new("message", " two spaces", string.Empty, null) };
        Assert.Equal(expected, Parse(text));
        Assert.Equal(expected, Parse(text, chunk: 1));
        Assert.Equal(expected, Parse(text, chunk: 3));
    }

    [Fact]
    public void BomIsDroppedOnce_FieldWithoutColonIsEmpty()
    {
        List<SseEvent> events = Parse("﻿data\ndata: x\n\n");
        Assert.Equal("\nx", Assert.Single(events).Data);
    }

    [Fact]
    public void EventWithoutData_IsNotDispatched()
    {
        Assert.Empty(Parse("event: info\nid: 7\n\n"));
        var parser = new SseParser();
        Parse("event: info\nid: 7\n\n", parser);
        Assert.Equal("7", parser.LastEventId); // the id still counts
    }

    [Fact]
    public void IdWithNulIsIgnored_BadRetryIsIgnored_UnknownFieldsAreIgnored()
    {
        var parser = new SseParser();
        List<SseEvent> events = Parse("id: good\ndata: 1\n\nid: bad\0id\nretry: 12a\nretry: -5\nfoo: bar\ndata: 2\n\n", parser);
        Assert.Equal(["good", "good"], events.Select(e => e.Id));
        Assert.All(events, e => Assert.Null(e.Retry));
        Assert.Null(parser.RetryMilliseconds);
    }

    [Fact]
    public void PartialEventAtConnectionEnd_IsDiscarded_ButLastIdAndRetrySurvive()
    {
        var parser = new SseParser();
        Assert.Single(Parse("id: e5\nretry: 4000\ndata: done\n\ndata: half", parser));
        parser.ResetConnection();
        Assert.Equal("e5", parser.LastEventId);
        Assert.Equal(4000, parser.RetryMilliseconds);
        Assert.Equal("next", Assert.Single(Parse("data: next\n\n", parser)).Data); // "half" was not glued on
    }

    [Fact]
    public void LineOrEventOverTheLimit_IsAProtocolError()
    {
        Assert.Throws<SseProtocolException>(() => Parse("data: " + new string('x', 2000) + "\n\n", new SseParser(1024)));
        Assert.Throws<SseProtocolException>(() => Parse(string.Concat(Enumerable.Repeat("data: " + new string('x', 300) + "\n", 5)), new SseParser(1024)));
        Assert.Single(Parse("data: " + new string('x', 1000) + "\n\n", new SseParser(1024)));
    }
}
