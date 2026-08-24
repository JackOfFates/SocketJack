using System.Reflection;
using JackONNX.HeirowSong;

namespace JackONNX.Tests;

[TestClass]
public sealed class JackOnnxHeirowSongMediaExecutorTests
{
    [TestMethod]
    public void ParseSnapshot_HandlesAceStepV018WrappedResult()
    {
        const string json = """
            {"data":[{"task_id":"job-1","result":"[{\"file\":\"/v1/audio?path=C%3A%5C%5Cmusic.wav\",\"status\":1,\"progress\":1.0,\"stage\":\"succeeded\"}]","status":1,"progress_text":"Saved audio (wav, 48000Hz)"}],"code":200,"error":null}
            """;

        object snapshot = Parse(json);

        Assert.AreEqual("succeeded", Property<string>(snapshot, "State"));
        Assert.AreEqual(100d, Property<double>(snapshot, "Progress"));
        CollectionAssert.AreEqual(new[] { "/v1/audio?path=C%3A%5C%5Cmusic.wav" }, Property<List<string>>(snapshot, "Paths"));
    }

    [TestMethod]
    public void ParseSnapshot_MapsNumericFailureStatus()
    {
        const string json = """
            {"data":[{"task_id":"job-2","result":"[]","status":2,"error":"generation failed"}],"code":200}
            """;

        object snapshot = Parse(json);

        Assert.AreEqual("failed", Property<string>(snapshot, "State"));
        Assert.AreEqual("generation failed", Property<string>(snapshot, "Error"));
    }

    private static object Parse(string json)
    {
        MethodInfo method = typeof(JackOnnxHeirowSongMediaExecutor).GetMethod("ParseSnapshot", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("ParseSnapshot was not found.");
        return method.Invoke(null, new object[] { json }) ?? throw new AssertFailedException("ParseSnapshot returned null.");
    }

    private static T Property<T>(object instance, string name) => (T)(instance.GetType().GetProperty(name)?.GetValue(instance)
        ?? throw new AssertFailedException(name + " was not found."));
}
