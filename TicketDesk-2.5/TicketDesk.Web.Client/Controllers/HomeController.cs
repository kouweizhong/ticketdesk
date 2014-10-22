﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using System.Web.Mvc;
using TicketDesk.Domain;
using TicketDesk.Domain.Model.Search;

namespace TicketDesk.Web.Client.Controllers
{
    public class HomeController : Controller
    {
        private SearchIndexer Searcher { get; set; }
        private TicketDeskContext Context { get; set; }
        public HomeController(SearchIndexer searcher, TicketDeskContext context)
        {
            Searcher = searcher;
            Context = context;
        }
        public ActionResult Index()
        {
            
            return View();
        }

        public ActionResult About()
        {
            return View();
        }

        //public async Task<ActionResult> Indexer()
        //{


        //    await Searcher.GenerateIndexAsync();
        //    return View("Index");
        //}
        
    }
}