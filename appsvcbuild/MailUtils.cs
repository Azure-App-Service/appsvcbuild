using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Azure.WebJobs.Host;
using SendGrid;
using SendGrid.Helpers.Mail;
using Microsoft.Extensions.Logging;

namespace appsvcbuild
{
    public class MailUtils
    {
        public SendGridClient _mailClient;
        private String _stack;
        public String _version { get; set; }

        public MailUtils(SendGridClient mailClient, String stack)
        {
            _mailClient = mailClient;
            _stack = stack;
        }

        public ILogger _log { get; set; }

        public async System.Threading.Tasks.Task SendSuccessMail(List<String> versions, String log)
        {
            List<EmailAddress> recipients = new List<EmailAddress>
            {
                new EmailAddress("patle@microsoft.com")
            };
            String subject = "";
            String content = "";
            if (versions.Count > 0)
            {
                subject = String.Format("appsvcbuild has built new {0} images", _stack);
                content = String.Format("new {0} images build {1}\n{2}", _stack, String.Join(", ", versions.ToArray()), log);
            }
            else
            {
                subject = String.Format("no new {0} images", _stack);
                content = String.Format("no new {0} images\n{1}", _stack, log);
            }

            await sendEmail(recipients, subject, content);
        }

        public async System.Threading.Tasks.Task SendFailureMail(String failure, String log)
        {
            List<EmailAddress> recipients = new List<EmailAddress>
            {
                new EmailAddress("patle@microsoft.com")
            };
            String subject = String.Format("{0} {1} appsvcbuild has failed", _stack, _version);
            await sendEmail(recipients, subject, String.Format("{0}\n{1}", failure, log));
        }

        private async System.Threading.Tasks.Task sendEmail(List<EmailAddress> recipients, String subject, String content)
        {
            EmailAddress from = new EmailAddress("appsvcbuild@microsoft.com", "appsvcbuild");
            List<EmailAddress> to = recipients;
            String plainTextContent = content;
            String htmlContent = content;
            SendGridMessage msg = MailHelper.CreateSingleEmailToMultipleRecipients(from, to, subject, plainTextContent, htmlContent);
            Response response = await _mailClient.SendEmailAsync(msg);
        }
    }
}
