
namespace projectApiAngular.Services
{
    public interface IKafkaProducerService
    {
        Task SendMessageAsync<T>(string key, T data);
    }
}