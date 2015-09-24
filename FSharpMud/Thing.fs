﻿module Thing

open Akka.Actor
open Akka.FSharp
open Messages
open Utils
open System


let handleInput player (input:string) =
    let parts = input.Split([|' '|],2,StringSplitOptions.None)
    let command = parts |> Seq.tryHead
    let message = parts |> Seq.skip(1) |> Seq.tryLast
    match command, message with
    | Some(str),Some(msg) ->
        match str with
        | "say"
        | "s" -> player <! Say(msg)
        | "take"
        | "get"
        | "t" -> player <! Take(msg)
        | "enter" -> player <! Enter(msg)
        | "drop"
        | "d" -> player <! Drop(msg)
        | "put" when msg.Contains(" in ") -> 
            let parts = msg.Split([|" in "|],2,StringSplitOptions.RemoveEmptyEntries)
            let target = parts.[0]
            let container = parts.[1]
            player <! Put(target,container)
        | other -> printfn "unknown command %A" other
    | Some(str),None ->        
        match str with
        | "look"
        | "l" -> player <! Look 
        | "exit" -> player <! Exit
        | "inventory"
        | "inv" -> player <! Inventory  
        | "where" -> player <! Where
        | "north"
        | "n" -> player <! Go("north")
        | "south"
        | "s" -> player <! Go("south")
        | "east"
        | "e" -> player <! Go("east")
        | "west"
        | "w" -> player <! Go("west")
        | other -> printfn "unknown command %A" other
    | _ -> ignore()




type ThingState = 
    { container : NamedObject
      output : IActorRef
      objectsYouHave : Set<NamedObject>
      objectsYouSee : Set<NamedObject>
      exitsYouHave : Set<NamedObject>
      exitsYouSee : Set<NamedObject> }

let notify target format (args : List<obj>) = target <! Notify(Message(format, args))

let thing (name : string) (mailbox : Actor<ThingMessage>) = 
    let self = mailbox.Self
    
    let namedSelf = 
        { name = name
          ref = self }
    
    let rec loop (state : ThingState) = 
        actor { 
            let! message = mailbox.Receive()
            match message with
            // Container actions
            | AddExit(exit) -> return! loop { state with exitsYouHave = state.exitsYouHave.Add(exit) }
            | SetContainerByActorRef(newContainer) -> newContainer <! AddContent(namedSelf)
            | AddContent(who) -> 
                for no in state.objectsYouHave do
                    no.ref <! AddedContent(who)
                let newObjectsYouHave = state.objectsYouHave.Add who
                who.ref <! NewContainerAssigned(namedSelf, newObjectsYouHave, state.exitsYouHave)
                return! loop { state with objectsYouHave = newObjectsYouHave }
            | RemoveContent(who, container) -> 
                for no in state.objectsYouHave |> Seq.except [ who ] do
                    no.ref <! RemovedContent(who, container)
                return! loop { state with objectsYouHave = state.objectsYouHave.Remove who }
            | ContainerNotify(message, except) -> 
                let targets = 
                    state.objectsYouHave
                    |> Seq.map (fun no -> no.ref)
                    |> Seq.except except
                for target in targets do
                    target <! Notify(message)
            | AddedContent(who) -> 
                notify self "{yellow}{0}{reset} appears" [ who.name ]
                return! loop { state with objectsYouSee = state.objectsYouSee.Add who }
            | RemovedContent(who, container) -> 
                if container <> namedSelf && not (state.objectsYouHave.Contains container) then 
                    notify self "{yellow}{0}{reset} disappears into {yellow}{1}{reset}" [ who.name; container.name ]
                return! loop { state with objectsYouSee = state.objectsYouSee.Remove who }
            | NewContainerAssigned(container, containerContent, exits) -> 
                self <! Look
                state.container.ref <! RemoveContent(namedSelf, container)
                return! loop { state with container = container
                                          objectsYouSee = containerContent
                                          exitsYouSee = exits }
            //Player / NPC Actions
            | Look -> 
                let objectNames = 
                    joinStrings (state.objectsYouSee
                                 |> Seq.except [ namedSelf ]
                                 |> Seq.map (fun no -> "{yellow}" + no.name + "{reset}")
                                 |> Seq.toArray)
                
                let exitNames = 
                    joinStrings (state.exitsYouSee
                                 |> Seq.map (fun no -> "{yellow}" + no.name + "{reset}")
                                 |> Seq.toArray)
                
                notify self "You are in {yellow}{0}{reset}" [ state.container.name ]
                notify self ("You see " + objectNames) [  ]
                notify self ("Exits are: " + exitNames) [  ]
            | Say(message) -> 
                state.container.ref <! ContainerNotify(Message("{yellow}{0}{reset} says {yellow}{1}{reset}", [ name; message ]), [ self ])
                notify self "You say {yellow}{0}{reset}" [ message ]
            | Take(nameOfObject) -> 
                let findResult = findObjectByName state.objectsYouSee nameOfObject
                match findResult with
                | Some(no) -> 
                    self <! AddContent(no)
                    notify self "You take {yellow}{0}{reset}" [ no.name ]
                | None -> notify self "Could not find {yellow}{0}{reset}" [ nameOfObject ]
            | Enter(nameOfObject) -> 
                let findResult = findObjectByName state.objectsYouSee nameOfObject
                match findResult with
                | Some(no) -> 
                    no.ref <! AddContent(namedSelf)
                    notify self "You enter {yellow}{0}{reset}" [ no.name ]
                | None -> notify self "Could not find {yellow}{0}{reset}" [ nameOfObject ]
            | Exit -> state.container.ref <! ExitContainer(namedSelf)
            | ExitContainer(who) -> state.container.ref <! AddContent(who)
            | Drop(nameOfObject) -> 
                let findResult = findObjectByName state.objectsYouHave nameOfObject
                match findResult with
                | Some(no) -> 
                    state.container.ref <! AddContent(no)
                    notify self "You drop {yellow}{0}{reset}" [ no.name ]
                | None -> notify self "Could not find {yellow}{0}{reset}" [ nameOfObject ]
            | Put(nameOfTarget, nameOfContainer) -> 
                let targets = 
                    (state.objectsYouSee
                     |> Seq.append state.objectsYouHave
                     |> Seq.except [ namedSelf ])
                
                let findResult = findObjectByName targets nameOfTarget
                match findResult with
                | Some(no1) -> 
                    let findResult2 = findObjectByName targets nameOfContainer
                    match findResult2 with
                    | Some(no2) -> 
                        no2.ref <! AddContent(no1)
                        notify self "You put {yellow}{0}{reset} in {yellow}{1}{reset}" [ no1.name; no2.name ]
                    | None -> notify self "Could not find {yellow}{0}{reset}" [ nameOfContainer ]
                | None -> notify self "Could not find {yellow}{0}{reset}" [ nameOfTarget ]
            | Inventory -> 
                let names = 
                    joinStrings (state.objectsYouHave
                                 |> Seq.map (fun no -> "{yellow}" + no.name + "{reset}")
                                 |> Seq.toArray)
                self <! Notify(Message("You have " + names, []))
            | Go(direction) -> 
                let exit = findObjectByName state.exitsYouSee direction
                match exit with
                | Some(no) -> no.ref <! AddContent(namedSelf)
                | None -> notify self "You can not go {yellow}{0}{reset}" [ direction ]
            //output stream actions
            | Notify(message) -> state.output <! message
            | SetOutput(newOutput) -> return! loop { state with output = newOutput }
            | Where -> notify self "You are in {yellow}{0}{reset}" [ state.container.name ]
            | Fight(nameOfTarget) -> failwith "Not implemented yet"
            | SetTarget(_) -> failwith "Not implemented yet"
            | AttackCurrentTarget -> failwith "Not implemented yet"
            | TakeDamage(attacker, damage) -> failwith "Not implemented yet"
            | NotifyCombatStatus -> failwith "Not implemented yet"
            | Died -> failwith "Not implemented yet"
            return! loop state
        }
    
    let emptySet = Set.empty<NamedObject>
    let nobody = ActorRefs.Nobody :> IActorRef
    
    let namedNobody = 
        { name = "void"
          ref = nobody }
    loop { container = namedNobody
           output = nobody
           objectsYouHave = emptySet
           objectsYouSee = emptySet
           exitsYouHave = emptySet
           exitsYouSee = emptySet }
