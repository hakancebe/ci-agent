using System.Runtime.CompilerServices;

// Webhook imza doğrulama, payload ayrıştırma ve kuyruk mantığı internal ama test
// edilmesi ŞART olan parçalar - CiAgent.Core'daki aynı yaklaşım (bkz. oradaki
// AssemblyInfo.cs). Bunlar servisin dış API'si değil, iç mekanizması; public
// yapmak yerine testlere açıyoruz.
[assembly: InternalsVisibleTo("CiAgent.Tests")]
