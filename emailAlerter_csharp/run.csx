#r "Microsoft.WindowsAzure.Storage"
#r "SendGrid"
#r "Newtonsoft.Json"
using Microsoft.WindowsAzure;
using Newtonsoft.Json;
using Microsoft.WindowsAzure.Storage.Queue;
using SendGrid.Helpers.Mail;
using System;
using System.Text;
public static void Run(CloudQueueMessage myQueueItem, 
    DateTimeOffset expirationTime, 
    DateTimeOffset insertionTime, 
    DateTimeOffset nextVisibleTime,
    string queueTrigger,
    string id,
    string popReceipt,
    int dequeueCount, TraceWriter log, out Mail message)
{
    log.Info($"C# Queue trigger function processed: {myQueueItem}");
        StateEntity state =JsonConvert.DeserializeObject<StateEntity>(myQueueItem.AsString);
        log.Info(state.UrlName);
        log.Info(state.Url);
        log.Info(state.Date.ToString());
        log.Info(state.Description);
        
    message = new Mail();
   StringBuilder text = new StringBuilder();
       text.AppendLine($"<h3>The following resources had or have a status change: </h3>");
       text.AppendLine($"<p>UrlName : {state.UrlName}</P>");
       text.AppendLine($"<p>Url : {state.Url}</P>");
       text.AppendLine($"<p>Poll Status : {state.Status}</p>");
       text.AppendLine($"<p>Status Description : {state.Description}</p>");
       text.AppendLine("<hr/>");
    
    Content content = new Content
    {
        Type = "text/html",
        Value = text.ToString()
    };
    message.AddContent(content);
   

         
}

public class StateEntity
{
    public StateEntity(){
        
    }
    public StateEntity(string url,string urlName, string status, string description){
        
        this.UrlName = urlName;
        this.Url = url;
        this.Status = status;
        this.Description = description;
        this.Date = DateTime.Now;
    }
    public string UrlName {get;set;}
    public string Url {get;set;}
    public string Status {get;set;}
    public string Description {get;set;}
    public DateTime Date {get;set;}
}

