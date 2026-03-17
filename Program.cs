// See https://aka.ms/new-console-template for more information
using ESI.NET;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace EveMarketBot
{
    class Program
    {
        static Task Main(string[] args)=>
             CreateHostBuilder(args).Build()
                .RunAsync();
        

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
            .ConfigureServices((hostContext,myServices) =>
            {
                myServices.AddHostedService<Worker>();
                myServices.AddEsi(hostContext.Configuration.GetSection("EsiConfig"));
            });
    }
}