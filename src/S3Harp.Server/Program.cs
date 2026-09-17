using S3Harp.Server;

return S3HarpCommand
    .Create(settings =>
    {
        try
        {
            S3HarpApplication.Build(settings).Run();
            return 0;
        }
        catch (StartupException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    })
    .Parse(args)
    .Invoke();
