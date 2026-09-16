using S3Harp.Server;

try
{
    S3HarpApplication.Build(args).Run();
    return 0;
}
catch (StartupException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
