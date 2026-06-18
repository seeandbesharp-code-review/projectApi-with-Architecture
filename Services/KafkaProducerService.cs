using Confluent.Kafka;
using System.Text.Json;

namespace projectApiAngular.Services
{
    public class KafkaProducerService : IKafkaProducerService
    {
        private readonly IProducer<string, string> _producer;
        private readonly string? _topicName;

        public KafkaProducerService(IConfiguration configuration)
        {
            // 1. שליפת הנתונים מה-appsettings
            var bootstrapServers = configuration["KafkaSettings:BootstrapServers"];
            _topicName = configuration["KafkaSettings:TopicName"];

            if (_topicName == null)
            {
                throw new InvalidOperationException();
            }
            if (bootstrapServers == null)
            {
                throw new InvalidOperationException();
            }

            // 2. יצירת אובייקט ProducerConfig כפי שנדרש
            var config = new ProducerConfig
            {
                BootstrapServers = bootstrapServers
            };

            // 3. שימוש במחלקה הרשמית ProducerBuilder
            _producer = new ProducerBuilder<string, string>(config).Build();
        }

        public async Task SendMessageAsync<T>(string key, T data)
        {
            var serializedData = JsonSerializer.Serialize(data);

            var message = new Message<string, string>
            {
                Key = key,
                Value = serializedData
            };

            // שליחת ההודעה ל-Kafka באופן אסינכרוני
            await _producer.ProduceAsync(_topicName, message);
        }
    }
}
