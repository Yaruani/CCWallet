﻿using CCWallet.DiscordBot.Services;
using CCWallet.DiscordBot.Utilities;
using CCWallet.DiscordBot.Utilities.Discord;
using Discord;
using Discord.Commands;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;

namespace CCWallet.DiscordBot.Modules
{
    public abstract class CurrencyModuleBase : ModuleBase
    {
        protected UserWallet Wallet { get; private set; }
        protected abstract Network Network { get; }

        protected override void BeforeExecute(CommandInfo command)
        {
            base.BeforeExecute(command);

            Wallet = Provider.GetService<WalletService>().GetUserWallet(Network, Context.User);
            Wallet.CultureInfo = Catalog.CultureInfo;
        }

        [Command(BotCommand.Help)]
        [RequireContext(ContextType.DM | ContextType.Group | ContextType.Guild)]
        [RequireBotPermission(ChannelPermission.SendMessages | ChannelPermission.AddReactions | ChannelPermission.EmbedLinks)]
        public virtual async Task CommandHelpAsync(string command = null)
        {
            throw new NotImplementedException();
        }

        [Command(BotCommand.Balance)]
        [RequireContext(ContextType.DM | ContextType.Group | ContextType.Guild)]
        [RequireBotPermission(ChannelPermission.SendMessages | ChannelPermission.AddReactions | ChannelPermission.EmbedLinks)]
        public virtual async Task CommandBalanceAsync()
        {
            await Context.Channel.TriggerTypingAsync();
            await Wallet.UpdateBalanceAsync();

            await ReplySuccessAsync(_("Your {0} balance.", Wallet.Currency.Name), CreateEmbed(new EmbedBuilder()
            {
                Color = Color.DarkPurple,
                Title = _("Balance"),
                Description = String.Join("\n", new[]
                {
                    _("Only confirmed balances are available."),
                    _("Slight balance errors may occur due to network conditions."),
                }),
                Fields = new List<EmbedFieldBuilder>
                {
                    new EmbedFieldBuilder().WithName(_("Owner")).WithValue(GetName(Context.User)),
                    new EmbedFieldBuilder().WithName(_("Balance")).WithValue(Wallet.TotalBalance),
                    new EmbedFieldBuilder().WithIsInline(true).WithName(_("Confirmed")).WithValue(Wallet.ConfirmedBalance),
                    new EmbedFieldBuilder().WithIsInline(true).WithName(_("Confirming")).WithValue(Wallet.PendingBalance),
                    new EmbedFieldBuilder().WithIsInline(true).WithName(_("Unconfirmed")).WithValue(Wallet.UnconfirmedBalance),
                },
            }));
        }

        [Command(BotCommand.Deposit)]
        [RequireContext(ContextType.DM | ContextType.Group | ContextType.Guild)]
        [RequireBotPermission(ChannelPermission.SendMessages | ChannelPermission.AddReactions | ChannelPermission.EmbedLinks)]
        public virtual async Task CommandDepositAsync()
        {
            await Context.Channel.TriggerTypingAsync();
            await ReplySuccessAsync(String.Join("\n", new[]
            {
                _("Your {0} address.", Wallet.Currency.Name),
                Wallet.Address.ToString(),
            }), CreateEmbed(new EmbedBuilder()
            {
                Color = Color.DarkBlue,
                Title = _("Deposit Address"),
                Description = String.Join("\n", new[]
                {
                    _("Your deposit address is {0}.", Wallet.Address),
                }),
                Fields = new List<EmbedFieldBuilder>
                {
                    new EmbedFieldBuilder().WithName(_("Owner")).WithValue(GetName(Context.User)),
                    new EmbedFieldBuilder().WithName(_("Deposit Address")).WithValue($"```{Wallet.Address}```"),
                },
            }));
        }

        [Command(BotCommand.Withdraw)]
        [RequireContext(ContextType.DM | ContextType.Group | ContextType.Guild)]
        [RequireBotPermission(ChannelPermission.SendMessages | ChannelPermission.AddReactions | ChannelPermission.EmbedLinks)]
        public virtual async Task CommandWithdrawAsync(string address, decimal amount)
        {
            await Context.Channel.TriggerTypingAsync();
            await Wallet.UpdateBalanceAsync();
            var outputs = new Dictionary<string, decimal>() { { address, amount } };
            TryTransfer(outputs, out var tx, out var error);

            await ReplyTransferAsync(new EmbedBuilder()
            {
                Title = _("Withdraw"),
            }, tx, outputs, amount, error);
        }

        [Command(BotCommand.Tip)]
        [RequireContext(ContextType.Guild | ContextType.Group)]
        [RequireBotPermission(ChannelPermission.SendMessages | ChannelPermission.AddReactions | ChannelPermission.EmbedLinks)]
        public virtual async Task CommandTipAsync(IUser user, decimal amount, params string[] comment)
        {
            await Context.Channel.TriggerTypingAsync();
            await Wallet.UpdateBalanceAsync();

            var outputs = new Dictionary<IDestination, decimal>() { { GetAddress(user), amount } };
            TryTransfer(outputs, out var tx, out var error);

            await ReplyTransferAsync(new EmbedBuilder()
            {
                Title = _("Tip"),
            }, tx, new Dictionary<string, decimal>() { { GetName(user), amount } }, amount, error);
        }

        [Command(BotCommand.Rain)]
        [RequireContext(ContextType.Guild | ContextType.Group)]
        [RequireBotPermission(ChannelPermission.SendMessages | ChannelPermission.AddReactions | ChannelPermission.EmbedLinks)]
        public virtual async Task CommandRainAsync(decimal amount, params string[] comment)
        {
            Transaction tx = null;
            string error = null;
            var outputs = new Dictionary<IDestination, decimal>();
            var displayOutputs = new Dictionary<string, decimal>();

            try
            {
                Wallet.ValidateAmount(amount, true);
                if (Wallet.Currency.MinRainAmount > amount)
                {
                    throw new ArgumentOutOfRangeException(null, "Lower than the minimum rain amount.");
                }

                await Context.Channel.TriggerTypingAsync();
                IEnumerable<IUser> users = await Context.Channel.GetUsersAsync().FlattenAsync();
                await Wallet.UpdateBalanceAsync();
                var targets = new List<IUser>();

                foreach (var u in users)
                {
                    if (u.IsBot || u.Status != Discord.UserStatus.Online || u.Id == Wallet.User.Id)
                    {
                        // exclude if the user is bot, or not online, or sender.
                        continue;
                    }
                    targets.Add(u);
                }

                if (targets.Count > 0)
                {
                    var rand = new Random();
                    while (targets.Count > Wallet.Currency.MaxRainUsers)
                    {
                        targets.RemoveAt(rand.Next() % targets.Count);
                    }

                    var amountPerUser = Decimal.Floor(amount / targets.Count * Wallet.Currency.BaseAmountUnit) / Wallet.Currency.BaseAmountUnit;
                    foreach (var user in targets)
                    {
                        outputs.Add(GetAddress(user), amountPerUser);
                        displayOutputs.Add(GetName(user), amountPerUser);
                    }
                    TryTransfer(outputs, out tx, out error);
                }
                else
                {
                    error = _("There are no users.");
                }
            }
            catch (ArgumentOutOfRangeException e)
            {
                error = _("Invalid amount. {0}", _(e.Message));
            }

            await ReplyTransferAsync(new EmbedBuilder()
            {
                Title = _("Rain"),
            }, tx, displayOutputs, amount, error);
        }

        [Command(BotCommand.SignMessage)]
        [RequireContext(ContextType.DM)]
        [RequireBotPermission(ChannelPermission.SendMessages | ChannelPermission.AddReactions | ChannelPermission.EmbedLinks)]
        public virtual async Task CommandSignMessageAsync([Remainder] string message)
        {
            await Context.Channel.TriggerTypingAsync();

            var signature = Wallet.SignMessage(message);

            await ReplySuccessAsync(_("Message signed. Generate signature is `{0}`", signature), CreateEmbed(new EmbedBuilder()
            {
                Color = Color.DarkPurple,
                Title = _("Sign Message"),
                Description = String.Join("\n", new[]
                {
                    _("Details of the signed content are as follows."),
                }),
                Fields = new List<EmbedFieldBuilder>
                {
                    new EmbedFieldBuilder().WithName(_("Address")).WithValue($"```{Wallet.Address}```"),
                    new EmbedFieldBuilder().WithName(_("Message")).WithValue($"```{message}```"),
                    new EmbedFieldBuilder().WithName(_("Signature")).WithValue($"```{signature}```"),
                },
            }));

        }

        protected virtual async Task ReplyTransferAsync(EmbedBuilder builder, Transaction tx, Dictionary<IDestination, decimal> outputs, decimal totalAmount, string error)
        {
            var convert = new Dictionary<string, decimal>();
            foreach (var output in outputs)
            {
                convert.Add(output.Key.ToString(), output.Value);
            }
            await ReplyTransferAsync(builder, tx, convert, totalAmount, error);
        }
        protected virtual async Task ReplyTransferAsync(EmbedBuilder builder, Transaction tx, Dictionary<string, decimal> outputs, decimal totalAmount, string error)
        {
            var result = error == String.Empty;

            builder.AddField(_("Result"), result ? _("Success") : _("Failed"));
            builder.AddField(_("From"), GetName(Context.User));
            if (outputs.Count > 1)
            {
                builder.AddField(_("To"), outputs.Count + " " + _("users"));
            }
            else if (outputs.Count == 1)
            {
                builder.AddField(_("To"), outputs.First().Key);
            }

            builder.AddField(_("Amount"), Wallet.FormatAmount(totalAmount), true);

            if (outputs.Count > 1)
            {
                var checkAmount = outputs.First().Value;
                var flag = true;
                foreach(var output in outputs)
                {
                    if(output.Value != checkAmount)
                    {
                        flag = false;
                        break;
                    }
                }
                if (flag)
                {
                    builder.AddField(_("Amount / User"), Wallet.FormatAmount(checkAmount), true);
                }
            }

            if (tx != null)
            {
                builder.AddField(_("Fee"), Wallet.FormatMoney(Wallet.GetFee(tx)), true);
                builder.AddField(_("Transaction"), tx.GetHash());
            }

            if (result)
            {
                builder.Color = Color.DarkerGrey;
                builder.Description = String.Join("\n", new[]
                {
                    _("Sent {0}.", Wallet.Currency.Name),
                    _("It may take some time to receive an approved message from the network; you can also check the status with the Blockchain Explorer."),
                });

                await ReplySuccessAsync(_("Sent {0}.", Wallet.Currency.Name), CreateEmbed(builder));
            }
            else
            {
                builder.Color = Color.Red;
                builder.Description = String.Join("\n", new[]
                {
                    _("Failed to send {0}.", Wallet.Currency.Name),
                    error,
                });

                await ReplyFailureAsync(_("Failed to send {0}.", Wallet.Currency.Name), CreateEmbed(builder));
            }
        }

        protected virtual bool TryTransfer(Dictionary<string, decimal> outputs, out Transaction tx, out string error)
        {
            try
            {
                var convert = new Dictionary<IDestination, decimal>();
                foreach (var output in outputs)
                {
                    convert.Add(BitcoinAddress.Create(output.Key, Network), output.Value);
                }
                return TryTransfer(convert, out tx, out error);
            }
            catch (FormatException)
            {
                error = _("Invalid address.");
            }

            tx = null;

            return false;
        }
        protected virtual bool TryTransfer(Dictionary<IDestination, decimal> outputs, out Transaction tx, out string error)
        {
            try
            {
                tx = Wallet.BuildTransaction(outputs);

                if (Wallet.TryBroadcast(tx, out var result))
                {
                    error = String.Empty;
                    return true;
                }

                error = _("Error - Transaction transmission failed. {0}", _(result));
            }
            catch (NotEnoughFundsException)
            {
                error = _("Insufficient funds.");
            }
            catch (ArgumentOutOfRangeException e)
            {
                error = _("Invalid amount. {0}", _(e.Message));
            }
            catch (ArgumentException e)
            {
                error = _("Error - Transaction generation failed. {0}", _(e.Message));
            }

            tx = null;

            return false;
        }

        protected virtual Embed CreateEmbed(EmbedBuilder embed = null)
        {
            return (embed ?? new EmbedBuilder())
                .WithAuthor(new EmbedAuthorBuilder()
                {
                    Name = _("{0} Wallet", Wallet.Currency.Name),
                    IconUrl = Wallet.Currency.IconUrl,
                })
                .WithFooter(new EmbedFooterBuilder()
                {
                    Text = _("CCWallet ({0} Module)", Wallet.Currency.Name),
                    IconUrl = Context.Client.CurrentUser.GetAvatarUrl(),
                })
                .WithThumbnailUrl(GetAvatarUrl(Context.User))
                .WithCurrentTimestamp()
                .Build();
        }

        protected virtual string GetName(IUser user)
        {
            var full = $"{user.Username}#{user.Discriminator}";
            var nick = (user as IGuildUser)?.Nickname;

            return nick != null ? $"{nick} ({full})" : full;
        }

        protected virtual string GetAvatarUrl(IUser user)
        {
            return String.IsNullOrEmpty(user.AvatarId)
                ? $"{DiscordConfig.CDNUrl}embed/avatars/{user.DiscriminatorValue % 5}.png"
                : user.GetAvatarUrl();
        }

        protected virtual BitcoinAddress GetAddress(IUser user)
        {
            return Provider.GetService<WalletService>().GetUserWallet(Network, user).Address;
        }
    }
}
