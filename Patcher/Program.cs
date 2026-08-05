using System;
using System.IO;
using System.Text.RegularExpressions;

public class Patcher {
    public static void Main() {
        string path = @"d:\DA.ATTT\secure-app\Program.cs";
        string content = File.ReadAllText(path);

        // Add using statements
        if (!content.Contains("using Microsoft.ML;")) {
            content = content.Replace("using System.Text.Json;", "using System.Text.Json;\r\nusing Microsoft.ML;\r\nusing Microsoft.ML.Data;");
        }

        // Replace IsMaliciousPayload
        string oldIsMaliciousStart = "bool IsMaliciousPayload(string input, out string attackType)";
        string oldIsMaliciousEnd = "return false;\r\n}";
        
        int startIndex = content.IndexOf(oldIsMaliciousStart);
        int endIndex = content.IndexOf(oldIsMaliciousEnd, startIndex) + oldIsMaliciousEnd.Length;
        
        if (startIndex != -1 && endIndex != -1) {
            string oldBlock = content.Substring(startIndex, endIndex - startIndex);
            
            string newIsMalicious = @"bool IsMaliciousPayload(string input, out string attackType)
{
    attackType = """";
    if (string.IsNullOrEmpty(input)) return false;

    string normalized = input;
    int decodeLimit = 3;
    for (int i = 0; i < decodeLimit; i++)
    {
        string previous = normalized;
        normalized = System.Net.WebUtility.UrlDecode(normalized);
        normalized = System.Net.WebUtility.HtmlDecode(normalized);
        if (normalized == previous) break;
    }

    normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @""/\*.*?\*/"", "" "", System.Text.RegularExpressions.RegexOptions.Singleline);
    normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @""<!--.*?-->"", "" "", System.Text.RegularExpressions.RegexOptions.Singleline);

    var result = MLWafEngine.Predict(normalized);
    if (result.Prediction && result.Probability > 0.7f)
    {
        attackType = $""AI-DETECTED (Prob: {result.Probability * 100:F1}%)"";
        return true;
    }

    return false;
}";
            content = content.Replace(oldBlock, newIsMalicious);
        }

        // Add ML classes at the end of the file
        string mlClasses = @"

public class WafData
{
    [LoadColumn(0)]
    public string Payload { get; set; }
    
    [LoadColumn(1)]
    public float Label { get; set; }
}

public class WafPrediction
{
    [ColumnName(""PredictedLabel"")]
    public bool Prediction { get; set; }
    
    public float Probability { get; set; }
    public float Score { get; set; }
}

public static class MLWafEngine
{
    private static MLContext _mlContext = new MLContext();
    private static ITransformer _model;
    private static PredictionEngine<WafData, WafPrediction> _predictionEngine;
    private static readonly string ModelPath = ""waf_model.zip"";
    private static readonly string DataPath = ""dataset.csv"";

    public static void Initialize()
    {
        if (File.Exists(ModelPath))
        {
            Console.WriteLine(""[ML] Loading existing AI WAF model..."");
            _model = _mlContext.Model.Load(ModelPath, out var schema);
        }
        else
        {
            Console.WriteLine(""[ML] Training new AI WAF model from dataset..."");
            if (!File.Exists(DataPath))
            {
                Console.WriteLine(""[ML] ERROR: dataset.csv not found!"");
                return;
            }

            IDataView dataView = _mlContext.Data.LoadFromTextFile<WafData>(
                path: DataPath, 
                hasHeader: true, 
                separatorChar: ',');

            var pipeline = _mlContext.Transforms.Text.FeaturizeText(
                    outputColumnName: ""Features"", 
                    inputColumnName: nameof(WafData.Payload))
                .Append(_mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                    labelColumnName: nameof(WafData.Label), 
                    featureColumnName: ""Features""));

            _model = pipeline.Fit(dataView);
            _mlContext.Model.Save(_model, dataView.Schema, ModelPath);
            Console.WriteLine(""[ML] AI WAF Model trained and saved successfully."");
        }

        _predictionEngine = _mlContext.Model.CreatePredictionEngine<WafData, WafPrediction>(_model);
    }

    public static WafPrediction Predict(string payload)
    {
        if (_predictionEngine == null) return new WafPrediction { Prediction = false, Probability = 0f };
        return _predictionEngine.Predict(new WafData { Payload = payload });
    }
}
";
        if (!content.Contains("MLWafEngine")) {
            content += mlClasses;
        }

        // Initialize ML Engine at startup
        if (!content.Contains("MLWafEngine.Initialize()")) {
            content = content.Replace("app.Run();", "MLWafEngine.Initialize();\r\napp.Run();");
        }

        File.WriteAllText(path, content);
    }
}
