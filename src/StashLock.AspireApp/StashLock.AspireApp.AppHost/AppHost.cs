var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.StashLock_Server>("stashlock-server")
    .WithUrl("/admin", "Admin Dashboard");

builder.Build().Run();
