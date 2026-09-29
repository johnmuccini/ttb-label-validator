namespace Ttb.LabelParser;
internal static class Cli
{
    public static int Run(string[] args)
    {
        try
        {
            string? pdf = null, output = null, cropFile = null, outputName = null;
            bool sendGemini = false;
            for (int i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--input": pdf = Next(args, ref i); break;
                    case "--output":
                    case "--out": output = Next(args, ref i); break;
                    case "--name": outputName = Next(args, ref i); break;
                    case "--crops": cropFile = Next(args, ref i); break;
                    case "--send-gemini": sendGemini = true; break;
                    case "--help":
                    case "-h":
                        Console.WriteLine("Ttb.LabelParser --input file.pdf --output directory [--name output-base-name] [--crops regions.json] [--send-gemini]");
                        return 0;
                    default:
                        if (args[i].StartsWith("--") || pdf is not null) throw new ArgumentException("Unknown argument: " + args[i]);
                        pdf = args[i]; break;
                }
            if (pdf is null) throw new ArgumentException("--input is required.");
            if (output is null) throw new ArgumentException("--output is required.");
            pdf = Path.GetFullPath(pdf);
            var result = Pipeline.Prepare(pdf, Path.GetFullPath(output), outputName, cropFile, sendGemini);
            Console.WriteLine(result.JsonPath);
            Console.WriteLine($"Extracted {result.LabelCount} label image(s).");
            Console.WriteLine(sendGemini ? "Gemini send requested." : "Gemini request prepared; no API call made.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
    }
    static string Next(string[] args, ref int i) => ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
}
