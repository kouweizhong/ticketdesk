﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Text;
using MvcContrib.Pagination;
using TicketDesk.Domain.Models;
using TicketDesk.Domain.Repositories;
using TicketDesk.Domain.Utilities;

namespace TicketDesk.Domain.Services
{

    [Export(typeof(ITicketService))]
    [PartCreationPolicy(System.ComponentModel.Composition.CreationPolicy.NonShared)]
    public class TicketService : ITicketService
    {
        //TODO: Many of these methods take user names, display names, etc. as parameters... 
        //       but we have a reference to security here, so we could probably get that info from there instead

        /// <summary>
        /// Initializes a new instance of the <see cref="TdTicketService"/> class.
        /// </summary>
        /// <param name="ticketRepository">The ticket repository.</param>
        [ImportingConstructor]
        public TicketService
        (
            ISecurityService securityService,
            ITicketRepository ticketRepository,
            INotificationQueuingService notificationService,
            TicketSearchService search
        )
        {
            Security = securityService;
            Repository = ticketRepository;
            Notification = notificationService;
            Search = search;
            Repository.Saving += new EventHandler<TicketEventArgs>(TicketSaving);
        }

        private void TicketSaving(object sender, TicketEventArgs e)
        {
            Search.UpdateIndex(new[] { e.Ticket });
        }

        #region ITicketService Members

        public INotificationQueuingService Notification { get; private set; }
        public TicketSearchService Search { get; private set; }
        public ISecurityService Security { get; private set; }

        /// <summary>
        /// Gets or sets the repository.
        /// </summary>
        /// <value>The repository.</value>
        public ITicketRepository Repository { get; private set; }

        /// <summary>
        /// Gets the ticket with the specified ID.
        /// </summary>
        /// <param name="ticketId">The ticket id to fetch.</param>
        /// <returns></returns>
        public Ticket GetTicket(int ticketId)
        {
            //TODO: Handle this in controllers
            if (!CheckSecurityForTicketActivity(null, TicketActivity.GetTicketInfo))
            {
                throw new RuleException("noAuth", "User is not authorized to retrieve ticket information");
            }
            return Repository.GetTicket(ticketId);

        }

        /// <summary>
        /// Gets a paged list of tickets.
        /// </summary>
        /// <param name="pageIndex">Index of the page.</param>
        /// <param name="listSettings">The list settings.</param>
        /// <returns></returns>
        public IPagination<Ticket> ListTickets(int pageIndex, TicketCenterListSettings listSettings)
        {
            //TODO: Handle this in controllers
            if (!CheckSecurityForTicketActivity(null, TicketActivity.GetTicketInfo))
            {
                throw new RuleException("noAuth", "User is not authorized to retrieve ticket information");
            }
            return Repository.ListTickets(pageIndex, listSettings.ItemsPerPage, listSettings.SortColumns, listSettings.FilterColumns);
        }


        /// <summary>
        /// Lists a paged list of tickets.
        /// </summary>
        /// <remarks>used by search</remarks>
        /// <param name="pageIndex">Index of the page.</param>
        /// <param name="pageSize">Size of the page.</param>
        /// <returns></returns>
        public IPagination<Ticket> ListTickets(int pageIndex, int pageSize)
        {
            return Repository.ListTickets(pageIndex, pageSize, null, null);
        }

        /// <summary>
        /// Gets a list of tickets from an ordered list of ticket IDs.
        /// </summary>
        /// <remarks>used by search</remarks>        
        /// <param name="orderedTicketList">The ordered ticket list.</param>
        /// <returns>
        /// Tickets in the same order as the supplied ticket IDs
        /// </returns>
        public IEnumerable<Ticket> ListTickets(SortedList<int, int> orderedTicketList)
        {
            return Repository.ListTickets(orderedTicketList);
        }

        /// <summary>
        /// Creates the new ticket.
        /// </summary>
        /// <param name="newTicket">The new ticket.</param>
        /// <param name="filesToAttach">The files to attach.</param>
        /// <param name="creatorUserName">User name of the creator.</param>
        /// <param name="creatorDisplayName">Display name of the creator.</param>
        /// <param name="ownerDisplayName">Display name of the owner.</param>
        /// <returns></returns>
        public int? CreateNewTicket(Ticket newTicket)
        {
            var rnv = new NameValueCollection();

            if (!CheckSecurityForTicketActivity(null, TicketActivity.Create))// don't need to check CreateOnBehalfOf separately
            {
                rnv.Add("noAuth", "User is not authorized to create a ticket");
            }

            var now = DateTime.Now;
            newTicket.CreatedBy = Security.CurrentUserName;
            newTicket.CreatedDate = now;
            newTicket.CurrentStatus = "Active";
            newTicket.CurrentStatusDate = now;
            newTicket.CurrentStatusSetBy = Security.CurrentUserName;
            newTicket.LastUpdateBy = Security.CurrentUserName;
            newTicket.LastUpdateDate = now;

            string[] tagsArr = TagUtility.GetTagsFromString(newTicket.TagList);

            foreach (string tag in tagsArr)
            {
                TicketTag tTag = new TicketTag();
                tTag.TagName = tag;
                newTicket.TicketTags.Add(tTag);
            }

            rnv.Add(ValidateTicketDetailFields(newTicket));// enforce field rules

            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }

            //comment
            TicketComment openingComment = (newTicket.Owner != Security.CurrentUserName) ?
                GetActivityComment(TicketActivity.CreateOnBehalfOf, TicketCommentFlag.CommentNotApplicable, null, newTicket.AssignedTo, newTicket.GetNotificationSubscribers(), Security.GetUserDisplayName(newTicket.Owner)) :
                GetActivityComment(TicketActivity.Create, TicketCommentFlag.CommentNotApplicable, null, newTicket.AssignedTo, newTicket.GetNotificationSubscribers());

            newTicket.TicketComments.Add(openingComment);

            AddAttachmentsToNewTicket(newTicket, Security.CurrentUserName);

            int? newTicketId = null;
            if (Repository.CreateTicket(newTicket, true))
            {
                newTicketId = newTicket.TicketId;
            }
            return newTicketId;
        }

        private void AddAttachmentsToNewTicket(Ticket ticket, string creatorUserName)
        {
            //files
            if (ticket.TicketAttachments != null && ticket.TicketAttachments.Count > 0)
            {
                var attaches = ticket.TicketAttachments.ToArray();
                ticket.TicketAttachments.Clear();
                foreach (var f in attaches)
                {
                    var ta = Repository.GetPendingAttachment(f.FileId);
                    ta.FileName = f.FileName;

                    ta.FileDescription = f.FileDescription;
                    ta.IsPending = false;
                    ticket.TicketAttachments.Add(ta);

                    //TODO: What to do when pending attachment isn't found?
                    ticket.TicketAttachments.Add(ta);
                }
            }
        }

        /// <summary>
        /// Adds the comment to ticket.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="commentBy">The user making the comment.</param>
        /// <param name="comment">The comment content.</param>
        /// <returns></returns>
        public bool AddCommentToTicket(Ticket ticket, string comment)
        {
            var rnv = new NameValueCollection();

            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.AddComment))
            {
                rnv.Add("noAuth", "User is not authorized to comment on the ticket");
            }
            if (string.IsNullOrEmpty(comment))
            {
                rnv.Add("comment", "A comment is required");
            }
            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }
            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = DateTime.Now;
            ticket.TicketComments.Add(GetActivityComment(TicketActivity.AddComment, TicketCommentFlag.CommentNotApplicable, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers()));

            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Requests the more information from the owner about the ticket.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="userName">Name of the user requesting more information.</param>
        /// <param name="comment">The comment.</param>
        /// <returns></returns>
        public bool RequestMoreInfoForTicket(Ticket ticket, string comment)
        {
            var rnv = new NameValueCollection();

            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.RequestMoreInfo))
            {
                rnv.Add("noAuth", "User is not authorized to request more information for the ticket");
            }
            if (string.IsNullOrEmpty(comment))
            {
                rnv.Add("comment", "A comment is required");
            }
            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }
            ticket.TicketComments.Add(GetActivityComment(TicketActivity.RequestMoreInfo, TicketCommentFlag.CommentNotApplicable, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers()));

            var now = DateTime.Now;
            ticket.CurrentStatus = "More Info";
            ticket.CurrentStatusDate = now;
            ticket.CurrentStatusSetBy = Security.CurrentUserName;
            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = now;

            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Supplies more information for the ticket.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="userName">Name of the user supplying more information.</param>
        /// <param name="comment">The comment.</param>
        /// <param name="markActive">if set to <c>true</c> marks ticket active again.</param>
        /// <returns></returns>
        public bool SupplyMoreInfoForTicket(Ticket ticket, string comment, bool markActive)
        {
            var rnv = new NameValueCollection();

            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.SupplyMoreInfo))
            {
                rnv.Add("noAuth", "User is not authorized to supply more information for the ticket");
            }
            if (string.IsNullOrEmpty(comment))
            {
                rnv.Add("comment", "A comment is required");
            }
            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }

            var markActiveText = (markActive) ? "and reactivated the ticket." : "without reactivating the ticket";
            ticket.TicketComments.Add(GetActivityComment(TicketActivity.SupplyMoreInfo, TicketCommentFlag.CommentNotApplicable, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers(), markActiveText));

            var now = DateTime.Now;
            if (markActive)
            {
                ticket.CurrentStatus = "Active";
                ticket.CurrentStatusDate = now;
                ticket.CurrentStatusSetBy = Security.CurrentUserName;

            }

            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = now;

            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Cancels the request for more information for ticket.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="userName">Name of the user cancelling the request for more information.</param>
        /// <param name="comments">The comments.</param>
        /// <returns></returns>
        public bool CancelMoreInfoForTicket(Ticket ticket, string comment)
        {
            var rnv = new NameValueCollection();

            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.CancelMoreInfo))
            {
                rnv.Add("noAuth", "User is not authorized to cancel the request for more information for the ticket");
            }

            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }

            ticket.TicketComments.Add(GetActivityComment(TicketActivity.CancelMoreInfo, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers()));

            var now = DateTime.Now;
            ticket.CurrentStatus = "Active";
            ticket.CurrentStatusDate = now;
            ticket.CurrentStatusSetBy = Security.CurrentUserName;
            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = now;

            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Takes the over ticket.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="userName">Name of the user taking over.</param>
        /// <param name="comment">optional comment contents.</param>
        /// <param name="priority">a priority or null if no change to priority.</param>
        /// <returns></returns>
        public bool TakeOverTicket(Ticket ticket, string comment, string priority)
        {
            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.TakeOver))//no need to check TakeOverWithPriority seperately
            {
                throw new RuleException("noAuth", "User is not authorized to take over a ticket");
            }

            if (!string.IsNullOrEmpty(priority))
            {
                ticket.Priority = priority;
            }

            var fromUser = (string.IsNullOrEmpty(ticket.AssignedTo)) ? string.Empty : " from " + Security.GetUserDisplayName(ticket.AssignedTo);

            TicketComment c = (string.IsNullOrEmpty(priority)) ?
                GetActivityComment(TicketActivity.TakeOver, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers(), fromUser) :
                GetActivityComment(TicketActivity.TakeOverWithPriority, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers(), fromUser, priority);

            ticket.TicketComments.Add(c);
            ticket.AssignedTo = Security.CurrentUserName;
            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = DateTime.Now;

            return Repository.UpdateTicket(ticket);
        }

        public bool AssignTicket(Ticket ticket, string comment, string assignTo, string priority)
        {
            bool hasPriority = (!string.IsNullOrEmpty(priority));

            TicketActivity activityEn;

            if (string.IsNullOrEmpty(ticket.AssignedTo))
            {
                activityEn = (hasPriority) ? TicketActivity.AssignWithPriority : TicketActivity.Assign;
            }
            else if (ticket.AssignedTo == Security.CurrentUserName)
            {
                activityEn = (hasPriority) ? TicketActivity.PassWithPriority : TicketActivity.Pass;
            }
            else
            {
                activityEn = (hasPriority) ? TicketActivity.ReAssignWithPriority : TicketActivity.ReAssign;
            }

            var rnv = new NameValueCollection();

            if (!CheckSecurityForTicketActivity(ticket, activityEn))
            {
                rnv.Add("noAuth", "User is not authorized to assign a ticket");
            }
            if (string.IsNullOrEmpty(assignTo))
            {
                rnv.Add("assignTo", "You must select a user to which you wish to assign the ticket");
            }
            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }
            ticket.TicketComments.Add(GetActivityComment(activityEn, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers(), Security.GetUserDisplayName(ticket.AssignedTo), Security.GetUserDisplayName(assignTo), priority));

            ticket.AssignedTo = assignTo;
            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = DateTime.Now;

            if (!string.IsNullOrEmpty(priority))
            {
                ticket.Priority = priority;
            }

            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Resolves the ticket.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="userName">Name of the user resolving the ticket.</param>
        /// <param name="comment">The comment.</param>
        /// <returns></returns>
        public bool ResolveTicket(Ticket ticket, string comment)
        {
            var rnv = new NameValueCollection();

            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.Resolve))
            {
                rnv.Add("noAuth", "User is not authorized to resolve ticket");
            }

            if (string.IsNullOrEmpty(comment))
            {
                rnv.Add("comment", "A comment is required");
            }

            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }

            ticket.TicketComments.Add(GetActivityComment(TicketActivity.Resolve, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers()));

            ticket.CurrentStatus = "Resolved";
            ticket.CurrentStatusSetBy = Security.CurrentUserName;
            ticket.CurrentStatusDate = DateTime.Now;
            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = ticket.CurrentStatusDate;
            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Closes the ticket.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="userName">Name of the user closing the ticket.</param>
        /// <param name="comment"></param>
        /// <returns></returns>
        public bool CloseTicket(Ticket ticket, string comment, bool force)
        {
            var rnv = new NameValueCollection();
            bool secCheck = false;
            if (force)
            {
                secCheck = CheckSecurityForTicketActivity(ticket, TicketActivity.ForceClose);
            }
            else
            {
                secCheck = CheckSecurityForTicketActivity(ticket, TicketActivity.Close);
            }

            if (!secCheck)
            {
                rnv.Add("noAuth", "User is not authorized to close ticket");
            }

            if (force && string.IsNullOrEmpty(comment))
            {
                rnv.Add("comment", "A comment is required");
            }
            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }

            TicketComment c = (force) ?
                GetActivityComment(TicketActivity.ForceClose, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers()) :
                GetActivityComment(TicketActivity.Close, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers());

            ticket.TicketComments.Add(c);

            ticket.CurrentStatus = "Closed";
            ticket.CurrentStatusSetBy = Security.CurrentUserName;
            ticket.CurrentStatusDate = DateTime.Now;
            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = ticket.CurrentStatusDate;

            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Gives up a ticket and marks it unassigned again.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="userName">Name of the user giving up the ticket.</param>
        /// <param name="comments">The comments.</param>
        /// <returns></returns>
        public bool GiveUpTicket(Ticket ticket, string comment)
        {
            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.GiveUp))
            {
                throw new RuleException("noAuth", "User is not authorized to modify ticket information");
            }
            if (string.IsNullOrEmpty(comment))
            {
                throw new RuleException("comment", "A comment is required");
            }

            ticket.TicketComments.Add(GetActivityComment(TicketActivity.GiveUp, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers()));

            ticket.AssignedTo = null;
            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = DateTime.Now;

            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Reopen ticket.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="userName">Name of the user re-opening the ticket.</param>
        /// <param name="comment">The comment.</param>
        /// <param name="reopenAssignedToUser">if set to <c>true</c> reopen and assign to the reopening user.</param>
        /// <param name="reopenOwnedByUser">if set to <c>true</c> reopen and set owner to the reopening user.</param>
        /// <returns></returns>
        public bool ReOpenTicket(Ticket ticket, string comment, bool reopenAssignedToUser, bool reopenOwnedByUser)
        {
            var rnv = new NameValueCollection();
            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.ReOpen))
            {
                rnv.Add("noAuth", "User is not authorized to re-open ticket");
            }
            if (string.IsNullOrEmpty(comment))
            {
                rnv.Add("comment", "A comment is required");
            }

            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }

            //If user is already owner, prevent "as the owner" part of event text
            if (Security.CurrentUserName == ticket.Owner)
            {
                reopenOwnedByUser = false;
            }

            var reopenAssignedToUserText = (reopenAssignedToUser) ? " and assigned it to themself" : string.Empty;
            var reopenOwnedByUserText = (reopenOwnedByUser) ? " as the owner" : string.Empty;


            //TODO: if reopened by a user that is not help desk, and re-opener is not 
            //          the previous owner reopen with this user as the owner

            ticket.TicketComments.Add(GetActivityComment(TicketActivity.ReOpen, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers(), reopenOwnedByUserText, reopenAssignedToUserText));

            ticket.AssignedTo = (reopenAssignedToUser) ? Security.CurrentUserName : null;
            if (reopenOwnedByUser)
            {
                ticket.Owner = Security.CurrentUserName;
            }

            ticket.CurrentStatus = "Active";
            ticket.CurrentStatusSetBy = Security.CurrentUserName;
            ticket.CurrentStatusDate = DateTime.Now;
            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = ticket.CurrentStatusDate;

            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Modifies the attachments for a ticket.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="userName">Name of the user modifying attachments.</param>
        /// <param name="comment">The comment.</param>
        /// <param name="attachments">The list attachment files with the desired state of the attachments</param>
        /// <returns></returns>
        public bool ModifyAttachmentsForTicket(Ticket ticket, string comment, List<TicketAttachment> attachments)
        {
            List<string> changeComments = new List<string>();

            var rnv = new NameValueCollection();
            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.ModifyAttachments))
            {
                rnv.Add("noAuth", "User is not authorized to modify the ticket's attachments");
            }



            var attachmentsToRemove = new List<TicketAttachment>();

            bool attachmentsChanged = false;
            foreach (var ticketAttachment in ticket.TicketAttachments.Where(ta => !ta.IsPending || ta.IsPending && ta.UploadedBy == Security.CurrentUserName))
            {
                var attMod = attachments.SingleOrDefault(a => a.FileId == ticketAttachment.FileId);
                if (attMod == null)// an attachment on the ticket is not in the list of attachments supplied, assume it is to be removed
                {
                    attachmentsToRemove.Add(ticketAttachment);
                }
                else//attachment in modified collection, assume it is to be modified
                {

                    if (ticketAttachment.FileName != attMod.FileName)
                    {
                        attachmentsChanged = true;
                        if (!ticketAttachment.IsPending)//add comment about changed attachment only of !IsPending
                        {
                            changeComments.Add(string.Format("changed file name from {0} to {1}", ticketAttachment.FileName, attMod.FileName));
                        }
                        ticketAttachment.FileName = attMod.FileName;
                    }
                    if (ticketAttachment.FileDescription != attMod.FileDescription)
                    {
                        attachmentsChanged = true;
                        if (!ticketAttachment.IsPending)//add comment about changed attachment only of !IsPending
                        {
                            changeComments.Add(string.Format("changed file description for file: {0}", attMod.FileName));
                        }
                        ticketAttachment.FileDescription = attMod.FileDescription;
                    }

                    if (ticketAttachment.IsPending)
                    {
                        attachmentsChanged = true;
                        changeComments.Add(string.Format("added file: {0}", attMod.FileName));
                        ticketAttachment.IsPending = false;
                    }
                }
            }
            foreach (var killAtt in attachmentsToRemove)
            {
                attachmentsChanged = true;
                changeComments.Add(string.Format("removed file: {0}", killAtt.FileName));
                Repository.RemoveAttachment(killAtt, false);
                ticket.TicketAttachments.Remove(killAtt);
            }

            if (!attachmentsChanged)
            {
                rnv.Add("attachments", "No changes to ticket's attachments were found.");
            }
            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }

            var ticketComment = GetActivityComment(TicketActivity.ModifyAttachments, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers());
            StringBuilder sb = new StringBuilder();
            foreach (string s in changeComments)
            {
                sb.Append(string.Format("- {0}\n", s));
            }
            if (ticketComment.Comment.Length > 0)
            {
                sb.Append("\n******\n");
                sb.Append(ticketComment.Comment);
            }
            ticketComment.Comment = sb.ToString();

            //TODO: add comment content for each change made
            ticket.TicketComments.Add(ticketComment);

            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = DateTime.Now;

            return Repository.UpdateTicket(ticket);
        }

        /// <summary>
        /// Edits the ticket.
        /// </summary>
        /// <param name="ticket">The ticket being edited with updated values.</param>
        /// <param name="comment">The comment.</param>
        /// <returns></returns>
        public bool EditTicketDetails(Ticket ticket, string comment)
        {

            var changes = Repository.GetTicketChanges(ticket);

            var rnv = new NameValueCollection();
            if (!CheckSecurityForTicketActivity(ticket, TicketActivity.EditTicketInfo))
            {
                rnv.Add("noAuth", "User is not authorized to edit ticket details");
            }

            //There are only certain fields we allow to be changed, check rules
            List<string> allowedFieldChanges = new List<string> { "Title", "Details", "Priority", "Type", "Category", "Owner", "TagList" };
            foreach (var field in changes)
            {
                if (!allowedFieldChanges.Contains(field.Key))
                {
                    rnv.Add("ticketInfo", string.Format("Changes to field {0} are not allowed", field.Key));
                }
            }

            //handle taglist changed by removing all existing tags and rebuilding them
            //NOTE: We could examine the tags and just make removals and insertions as needed, but the
            //          actual performance suffers very little and is not worth the extra effort and complexity.
            if (changes.ContainsKey("TagList"))
            {
                Repository.ClearTags(ticket, false);

                string[] tagsArr = TagUtility.GetTagsFromString(ticket.TagList);
                foreach (string tag in tagsArr)
                {
                    TicketTag tTag = new TicketTag();
                    tTag.TagName = tag;
                    ticket.TicketTags.Add(tTag);
                }
                ticket.TagList = string.Join(",", tagsArr);//in case the tags array trimmed spaces and such
            }

            rnv.Add(ValidateTicketDetailFields(ticket));//check the field rules 

            if (changes.Count < 1)
            {
                rnv.Add("ticketInfo", "No changes to ticket's details were found.");
            }

            if (rnv.Count > 0)
            {
                throw new RuleException(rnv);
            }

            var ticketComment = GetActivityComment(TicketActivity.EditTicketInfo, comment, ticket.AssignedTo, ticket.GetNotificationSubscribers());

            StringBuilder sb = new StringBuilder();
            foreach (var c in changes)
            {
                string v = null;
                PropertyInfo pi = ticket.GetType().GetProperty(c.Key);
                if (pi != null)
                {
                    v = pi.GetValue(ticket, null) as string;
                }
                if (c.Key != "Details" && c.Key != "Title" && c.Key != "TagList" && c.Key != "Owner")
                {
                    sb.Append(string.Format("- Changed {0} from \"{1}\" to \"{2}\"\n", c.Key.ConvertPascalCaseToFriendlyString(), c.Value, v));
                }
                else if (c.Key == "Owner")
                {
                    sb.Append(string.Format("- Changed {0} from \"{1}\" to \"{2}\"\n", c.Key.ConvertPascalCaseToFriendlyString(), Security.GetUserDisplayName(c.Value as string), Security.GetUserDisplayName(v)));
                }
                else //details, title, and tags are long strings so we don't include the full text of the old and new values
                {
                    sb.Append(string.Format("- Changed {0}\n", c.Key.ConvertPascalCaseToFriendlyString()));
                }
            }
            if (ticketComment.Comment.Length > 0)
            {
                sb.Append("\n******\n");
                sb.Append(ticketComment.Comment);
            }
            ticketComment.Comment = sb.ToString();

            ticket.TicketComments.Add(ticketComment);

            ticket.LastUpdateBy = Security.CurrentUserName;
            ticket.LastUpdateDate = DateTime.Now;

            return Repository.UpdateTicket(ticket);
        }

        public TicketAttachment GetAttachment(int fileId)
        {
            return Repository.GetTicketAttachment(fileId);
        }

        #endregion
        /// <summary>
        /// Checks the security for ticket activity for the current user.
        /// </summary>
        /// <param name="ticket">The ticket.</param>
        /// <param name="activity">The activity.</param>
        /// <returns></returns>
        public bool CheckSecurityForTicketActivity(Ticket ticket, TicketActivity activity)
        {
            return CheckSecurityForTicketActivity(ticket, activity, Security.CurrentUserName);
        }

        /// <summary>
        /// Checks the security for ticket activity for the specified user.
        /// </summary>
        /// <param name="ticket">The ticket, or null if checking an operation that isn't reliant on any specific ticket's state (such as NoChange, GetTicketInfo, etc).</param>
        /// <param name="activity">The activity.</param>
        /// <param name="userName">Name of the user to check.</param>
        /// <returns></returns>
        public bool CheckSecurityForTicketActivity(Ticket ticket, TicketActivity activity, string userName)
        {
            bool isAllowed = false;

            if (Security.IsInValidTdUserRole())//short-cut whole thing if not a valid TD user role
            {
                if (ticket == null)//some ops might supply no ticket, so we can create a dummy ticket instead
                {
                    ticket = new Ticket();
                }
                bool isAssigned = (!string.IsNullOrEmpty(ticket.AssignedTo));
                bool isOpen = (ticket.CurrentStatus != "Resolved" && ticket.CurrentStatus != "Closed");
                bool isAssignedToMe = (!string.IsNullOrEmpty(ticket.AssignedTo) && ticket.AssignedTo == userName);
                bool isOwnedByMe = (ticket.Owner == userName);
                bool isMoreInfo = (ticket.CurrentStatus == "More Info");
                bool isResolved = (ticket.CurrentStatus == "Resolved");

                switch (activity)
                {
                    case TicketActivity.NoChange:
                        isAllowed = true;
                        break;
                    case TicketActivity.GetTicketInfo:
                        isAllowed = true;
                        break;
                    case TicketActivity.Create:
                        isAllowed = true;
                        break;
                    case TicketActivity.CreateOnBehalfOf:
                        isAllowed = true;
                        break;
                    case TicketActivity.ModifyAttachments:
                        isAllowed = isOpen;
                        break;
                    case TicketActivity.EditTicketInfo:
                        isAllowed = isOpen && (Security.IsTdStaff() || isOwnedByMe);
                        break;
                    case TicketActivity.AddComment:
                        isAllowed = isOpen && !isMoreInfo;
                        break;
                    case TicketActivity.SupplyMoreInfo:
                        isAllowed = isMoreInfo;
                        break;
                    case TicketActivity.Resolve:
                        isAllowed = isOpen && !isMoreInfo && isAssignedToMe;
                        break;
                    case TicketActivity.RequestMoreInfo:
                        isAllowed = isOpen && !isMoreInfo && isAssignedToMe;
                        break;
                    case TicketActivity.CancelMoreInfo:
                        isAllowed = isMoreInfo && isAssignedToMe;
                        break;
                    case TicketActivity.Close:
                        isAllowed = isResolved && isOwnedByMe;
                        break;
                    case TicketActivity.ReOpen:
                        isAllowed = !isOpen;
                        break;
                    case TicketActivity.TakeOver:
                        isAllowed = (isOpen || isResolved) && !isAssignedToMe && Security.IsTdStaff();
                        break;
                    case TicketActivity.TakeOverWithPriority:
                        isAllowed = (isOpen || isResolved) && !isAssignedToMe && Security.IsTdStaff();
                        break;
                    case TicketActivity.Assign:
                        isAllowed = (isOpen || isResolved) && Security.IsTdStaff() && !isAssigned;
                        break;
                    case TicketActivity.AssignWithPriority:
                        isAllowed = (isOpen || isResolved) && Security.IsTdStaff() && !isAssigned;
                        break;
                    case TicketActivity.ReAssign:
                        isAllowed = (isOpen || isResolved) && Security.IsTdStaff() && isAssigned && !isAssignedToMe; ;
                        break;
                    case TicketActivity.ReAssignWithPriority:
                        isAllowed = (isOpen || isResolved) && Security.IsTdStaff() && isAssigned && !isAssignedToMe; ;
                        break;
                    case TicketActivity.Pass:
                        isAllowed = (isOpen || isResolved) && Security.IsTdStaff() && isAssignedToMe;
                        break;
                    case TicketActivity.PassWithPriority:
                        isAllowed = (isOpen || isResolved) && Security.IsTdStaff() && isAssignedToMe;
                        break;
                    case TicketActivity.GiveUp:
                        isAllowed = (isOpen || isResolved) && isAssignedToMe;
                        break;
                    case TicketActivity.ForceClose:
                        isAllowed = (isOpen || isResolved) && (isAssignedToMe || isOwnedByMe) && !(isResolved && isOwnedByMe);
                        break;
                }
            }
            return isAllowed;
        }

        /// <summary>
        /// Gets the activity comment. Infers a comment flag.
        /// </summary>
        /// <param name="activity">The activity.</param>
        /// <param name="commentBy">The user making the comment.</param>
        /// <param name="comment">The comment content.</param>
        /// <param name="args">Optional arguments to use as replacement values in the comment text.</param>
        /// <returns></returns>
        private TicketComment GetActivityComment(TicketActivity activity, string comment, string assignedTo, string[] notificationSubscribers, params string[] args)
        {
            var cFlag = (string.IsNullOrEmpty(comment)) ? TicketCommentFlag.CommentNotSupplied : TicketCommentFlag.CommentSupplied;
            return GetActivityComment(activity, cFlag, comment, assignedTo, notificationSubscribers, args);
        }

        /// <summary>
        /// Gets the activity comment.
        /// </summary>
        /// <param name="activity">The activity.</param>
        /// <param name="commentFlag">The comment flag.</param>
        /// <param name="commentBy">The user making the comment.</param>
        /// <param name="comment">The comment content.</param>
        /// <param name="args">Optional arguments to use as replacement values in the comment text.</param>
        /// <returns></returns>
        private TicketComment GetActivityComment(TicketActivity activity, TicketCommentFlag commentFlag, string comment, string assignedTo, string[] notificationSubscribers, params string[] args)
        {
            TicketComment c = new TicketComment();
            c.Comment = comment;
            c.CommentedBy = Security.CurrentUserName;
            c.CommentedDate = DateTime.Now;
            c.CommentEvent = TicketTextUtility.GetCommentText(activity, commentFlag, args);
            c.IsHtml = false;


            var isNewOrGiveUp = (assignedTo == null) && (activity == TicketActivity.GiveUp || activity == TicketActivity.Create || activity == TicketActivity.CreateOnBehalfOf);
            Notification.AddTicketEventNotifications(c, isNewOrGiveUp, notificationSubscribers);

            return c;
        }

        /// <summary>
        /// Adds the pending attachment.
        /// </summary>
        /// <param name="ticketId">The ticket id for an existing ticket, or null if pending attachment is for a new ticket that hasn't been created yet.</param>
        /// <param name="file">The file.</param>
        /// <returns>the FileId of the pending attachment</returns>
        public int AddPendingAttachment(int? ticketId, TicketAttachment file)
        {

            if (ticketId.HasValue)
            {
                file.TicketId = ticketId.Value;
            }
            file.IsPending = true;

            file.UploadedBy = Security.CurrentUserName;
            file.UploadedDate = DateTime.Now;

            Repository.AddPendingAttachment(file, true);

            return file.FileId;
        }

        /// <summary>
        /// Cleans up pending attachments that were not committed to a ticket in a timely manner.
        /// </summary>
        /// <param name="hoursOld">The number of hours old a pending attachment should be before being removed.</param>
        /// <returns></returns>
        public bool CleanUpDerelictAttachments(int hoursOld)
        {
            return Repository.CleanUpDerelictAttachments(hoursOld);
        }

        public NameValueCollection ValidateTicketDetailFields(Ticket ticket)
        {
            var rnv = new NameValueCollection();

            List<RuleException> brokenRules = new List<RuleException>();
            if (string.IsNullOrEmpty(ticket.Title))
            {
                rnv.Add("title", "A title is required");
            }
            if (string.IsNullOrEmpty(ticket.Category))
            {
                rnv.Add("category", "A category is required");
            }
            if (string.IsNullOrEmpty(ticket.Type))
            {
                rnv.Add("type", "A ticket type is required");
            }
            if (string.IsNullOrEmpty(ticket.Details))
            {
                rnv.Add("details", "Details are required");
            }
            if (string.IsNullOrEmpty(ticket.Owner))
            {
                rnv.Add("owner", "Owner is required.");
            }
            return rnv;
        }



        /// <summary>
        /// Gets possible suggestions for the last element of an incomplete taglist.
        /// </summary>
        /// <param name="partialTagList"></param>
        /// <param name="maxNumTagsToReturn">The max num tags to return.</param>
        /// <returns></returns>
        public string[] GetTagCompletionList(string partialTagList, int maxNumTagsToReturn)
        {
            //TODO: we can really speed up the autocomplete if we cache the results for the entire list of all distinct tags in advance

            List<string> returnList = new List<string>();

            string[] tags = partialTagList.Replace(" ,", ",").Replace(", ", ",").Split(',');//eliminate extra spaces around commas before split
            string textToCheck = tags[tags.Length - 1];//last element
            if (textToCheck.Trim().Length > 1)//only check if user has typed more than 1 character for the last item of the taglist
            {
                string fixedText = string.Empty;//all that stuff the user typed before the last comma in the text
                if (tags.Length > 1)
                {
                    StringBuilder sb = new StringBuilder();
                    for (int x = 0; x < tags.Length - 1; x++)
                    {
                        sb.Append(tags[x] + ",");
                    }
                    fixedText = sb.ToString();
                }

                var distinctTags = Repository.GetDistinctTagsStartingWith(textToCheck, maxNumTagsToReturn);

                foreach (var distinctTag in distinctTags)
                {
                    if (tags.Count(t => t.ToUpperInvariant() == distinctTag.ToUpperInvariant()) < 1)//eliminate any tags that were already used (that are in the fixedText).
                    {
                        returnList.Add(fixedText + distinctTag);//append the other items in the list plus the new tag possibilitiy
                    }
                }
            }
            return returnList.ToArray();
        }


    }
}