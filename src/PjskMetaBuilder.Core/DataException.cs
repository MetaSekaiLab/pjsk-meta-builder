namespace PjskMetaBuilder.Core;

/// <summary>输入数据有问题（包名不对、文件损坏、格式不认识）：换机器重试也没用。</summary>
public class DataException(string message) : Exception(message);
